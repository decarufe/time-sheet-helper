using System.Text.RegularExpressions;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>What the user just edited when an auto-fill is asked for.</summary>
public enum AutoFillTrigger { ProjectNumber, TaskNumber, Submit }

/// <summary>The numbers to send to <c>IForecastLookup.AutoFillAsync</c>; <see cref="Key"/> identifies the question (same key, same answer).</summary>
public sealed record AutoFillRequest(string? ProjectNumber, string? TaskNumber)
{
    public bool IsEmpty => ProjectNumber is null && TaskNumber is null;

    public string Key => $"{ProjectNumber}|{TaskNumber}";
}

/// <summary>What an auto-fill or a suggestion did: whether a field was filled, and what to tell the user (Forecast warnings, fields kept as typed).</summary>
public sealed record FillOutcome(bool Changed, IReadOnlyList<string> Notes);

/// <summary>
/// The fields of the "add a task" form (project, task, project number, task number) and the rule that fills them from Forecast.
/// <para>
/// <b>Rule: nothing the user typed is ever overwritten.</b> A field is filled from Forecast only when it is empty or still holds
/// what Forecast put there earlier (so a new number or suggestion replaces the previous auto-fill). A value typed by hand is kept
/// and the user is told (see <see cref="FillOutcome.Notes"/>). The one exception is an explicit choice: picking a project (or a
/// task) in a suggestion list sets that project's (or task's) name and number, because the pick is the instruction.
/// Editing an auto-filled name detaches its number, which would otherwise no longer describe it.
/// </para>
/// </summary>
public sealed class TaskFormState
{
    private static readonly Regex Digits = new(@"^\d+$");

    private readonly Field _project = new(NormalizeProjectNumber, isCode: false);
    private readonly Field _task = new(NormalizeTaskNumber, isCode: false);
    private readonly Field _projectCode = new(NormalizeProjectNumber, isCode: true);
    private readonly Field _taskCode = new(NormalizeTaskNumber, isCode: true);
    private int? _projectId;
    private string? _projectIdName;
    private int? _taskProjectId;

    public string Project { get => _project.Value; set => _project.Value = value ?? ""; }

    public string Task { get => _task.Value; set => _task.Value = value ?? ""; }

    public string ProjectCode { get => _projectCode.Value; set => _projectCode.Value = value ?? ""; }

    public string TaskCode { get => _taskCode.Value; set => _taskCode.Value = value ?? ""; }

    /// <summary>The Forecast project the project field stands for, to narrow a task search; null once the name was changed by hand.</summary>
    public int? ProjectIdForSearch => _projectId is { } id && Same(Project, _projectIdName) ? id : null;

    /// <summary>"1078" and "p1078" give "P1078"; empty stays empty; anything else is only upper-cased (the form reports it as invalid).</summary>
    public static string NormalizeProjectNumber(string? text) => Normalize(text, 'P');

    /// <summary>"35647" and "t35647" give "T35647".</summary>
    public static string NormalizeTaskNumber(string? text) => Normalize(text, 'T');

    /// <summary>Writes the canonical form of both numbers back into their fields.</summary>
    public void NormalizeCodes()
    {
        ProjectCode = NormalizeProjectNumber(ProjectCode);
        TaskCode = NormalizeTaskNumber(TaskCode);
    }

    /// <summary>
    /// The numbers to look up after an edit. A number that Forecast filled earlier is left out when the other one was just edited,
    /// so that a new project number is not contradicted by the task of the previous one (and the other way round).
    /// </summary>
    public AutoFillRequest BuildAutoFillRequest(AutoFillTrigger trigger)
    {
        var project = NormalizeProjectNumber(ProjectCode);
        var task = NormalizeTaskNumber(TaskCode);
        if (trigger == AutoFillTrigger.ProjectNumber && IsAuto(_taskCode))
            task = "";
        if (trigger == AutoFillTrigger.TaskNumber && IsAuto(_projectCode))
            project = "";
        return new AutoFillRequest(project.Length > 0 ? project : null, task.Length > 0 ? task : null);
    }

    /// <summary>Fills what Forecast found for the numbers typed, following the rule of the class.</summary>
    public FillOutcome ApplyAutoFill(ForecastAutoFillResult result)
    {
        var notes = new List<string>(result.Warnings);
        var changed = false;

        if (result.Project is { } project)
        {
            var name = _project.Fill(project.Name, force: false);
            if (name == FillResult.Kept)
            {
                notes.Add($"Le projet saisi « {Project} » est conservé (Forecast propose « {project.Name} »).");
            }
            else
            {
                changed |= name == FillResult.Filled;
                var code = _projectCode.Fill(project.Code, force: false);
                changed |= code == FillResult.Filled;
                if (code == FillResult.Kept)
                    notes.Add($"Le numéro de projet saisi {ProjectCode} est conservé (Forecast propose {project.Code}).");
                Bind(project.Id);
                if (result.Task is null)
                    DropStaleTask(project.Id);
            }
        }

        if (result.Task is { } task)
            changed |= FillTask(task, force: false, fillProject: result.Project is null, notes);

        return new FillOutcome(changed, notes);
    }

    /// <summary>A Forecast project picked in a list: its name and number are set (the pick is explicit); a task of another project is dropped if Forecast had filled it.</summary>
    public FillOutcome ChooseProject(ForecastProjectMatch project)
    {
        _project.Fill(project.Name, force: true);
        _projectCode.Fill(project.Code, force: true);
        Bind(project.Id);
        DropStaleTask(project.Id);
        return new FillOutcome(true, []);
    }

    /// <summary>A Forecast task picked in a list: its name and number are set, and its project fills the project fields unless the user typed another one.</summary>
    public FillOutcome ChooseTask(ForecastTaskMatch task)
    {
        var notes = new List<string>();
        FillTask(task, force: true, fillProject: true, notes);
        return new FillOutcome(true, notes);
    }

    /// <summary>A project already saved locally picked in a list: only the name is set (its number, if any, is kept by the saved project).</summary>
    public void ChooseKnownProject(string name)
    {
        _project.SetManual(name);
        _projectId = null;
        _projectIdName = null;
        if (IsAuto(_projectCode))
            _projectCode.Clear();
        if (_taskProjectId is not null)
            DropAutoTask();
    }

    /// <summary>The project name was typed in: an auto-filled number would no longer describe it, so it is dropped. Returns what to tell the user.</summary>
    public string? OnProjectEdited()
    {
        if (!IsAuto(_projectCode) || Same(Project, _project.Auto))
            return null;
        var dropped = _projectCode.Value;
        _projectCode.Clear();
        return $"Le numéro de projet {dropped} a été retiré : le nom du projet a changé.";
    }

    /// <summary>Same as <see cref="OnProjectEdited"/> for the task name.</summary>
    public string? OnTaskEdited()
    {
        if (!IsAuto(_taskCode) || Same(Task, _task.Auto))
            return null;
        var dropped = _taskCode.Value;
        _taskCode.Clear();
        return $"Le numéro de tâche {dropped} a été retiré : le nom de la tâche a changé.";
    }

    private bool FillTask(ForecastTaskMatch task, bool force, bool fillProject, List<string> notes)
    {
        var name = _task.Fill(task.Name, force);
        var changed = name == FillResult.Filled;
        changed |= _taskCode.Fill(task.Code, force) == FillResult.Filled;
        if (name == FillResult.Kept)
            notes.Add($"La tâche saisie « {Task} » est conservée (Forecast propose « {task.Name} »).");
        else
            _taskProjectId = task.ProjectId;

        if (fillProject && !string.IsNullOrWhiteSpace(task.ProjectName))
        {
            var project = _project.Fill(task.ProjectName, force: false);
            if (project == FillResult.Kept)
            {
                notes.Add($"La tâche {task.Code} appartient au projet {Join(task.ProjectCode, task.ProjectName)} : le projet saisi « {Project} » est conservé.");
            }
            else
            {
                changed |= project == FillResult.Filled;
                var code = _projectCode.Fill(task.ProjectCode ?? "", force: false);
                changed |= code == FillResult.Filled;
                if (code == FillResult.Kept)
                    notes.Add($"Le numéro de projet saisi {ProjectCode} est conservé (la tâche {task.Code} est dans {task.ProjectCode}).");
                if (task.ProjectId is { } id)
                    Bind(id);
            }
        }

        return changed;
    }

    private void Bind(int projectId)
    {
        _projectId = projectId;
        _projectIdName = Project;
    }

    private void DropStaleTask(int projectId)
    {
        if (_taskProjectId is { } owner && owner != projectId)
            DropAutoTask();
    }

    private void DropAutoTask()
    {
        if (IsAuto(_task))
            _task.Clear();
        if (IsAuto(_taskCode))
            _taskCode.Clear();
        _taskProjectId = null;
    }

    private static bool IsAuto(Field field) => field.Auto is not null && Same(field.Value, field.Auto);

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static string Join(string? code, string? name) =>
        string.Join(" ", new[] { code, name }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string Normalize(string? text, char prefix)
    {
        var trimmed = (text ?? "").Trim().ToUpperInvariant();
        return Digits.IsMatch(trimmed) ? $"{prefix}{trimmed}" : trimmed;
    }

    private enum FillResult { Same, Filled, Kept }

    private sealed class Field(Func<string?, string> canonical, bool isCode)
    {
        public string Value { get; set; } = "";

        /// <summary>The last value Forecast put in the field; while the field still holds it, the user has not touched it.</summary>
        public string? Auto { get; private set; }

        public void Clear()
        {
            Value = "";
            Auto = null;
        }

        /// <summary>A value the user chose among his own, not one Forecast supplied: later fills do not replace it.</summary>
        public void SetManual(string value)
        {
            Value = value;
            Auto = null;
        }

        public FillResult Fill(string value, bool force)
        {
            if (string.IsNullOrWhiteSpace(value))
                return FillResult.Same;

            if (string.IsNullOrWhiteSpace(Value) || force || Same(Value, Auto))
            {
                var same = Same(Value, value);
                Value = value;
                Auto = value;
                return same ? FillResult.Same : FillResult.Filled;
            }

            if (Same(Value, value))
                return FillResult.Same;

            // "1078" typed by the user, "P1078" found by Forecast: the same number, written in full.
            if (isCode && canonical(Value) == canonical(value))
            {
                Value = value;
                return FillResult.Filled;
            }

            return FillResult.Kept;
        }
    }
}
