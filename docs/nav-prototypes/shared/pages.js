/* Noyau partagé des prototypes : pages réelles de l'application, contenus factices, routage par hash, thème, actions. */
(function () {
  "use strict";

  const ICONS = {
    semaine: '<rect x="3" y="4" width="18" height="17" rx="2"/><path d="M3 9h18M8 2v4M16 2v4M7 13h2M11 13h2M15 13h2M7 17h2M11 17h2"/>',
    jour: '<rect x="3" y="4" width="18" height="17" rx="2"/><path d="M3 9h18M8 2v4M16 2v4"/><circle cx="12" cy="15" r="2" fill="currentColor"/>',
    soumettre: '<path d="M22 2 11 13"/><path d="M22 2l-7 20-4-9-9-4z"/>',
    regles: '<path d="M4 6h16M7 12h10M10 18h4"/>',
    taches: '<path d="M9 6h12M9 12h12M9 18h12"/><path d="m3 6 1.5 1.5L7 5M3 12l1.5 1.5L7 11M3 18l1.5 1.5L7 17"/>',
    sources: '<path d="M9 2v6M15 2v6M6 8h12v4a6 6 0 0 1-12 0z"/><path d="M12 18v4"/>',
    activite: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
    menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
    search: '<circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/>',
    chevL: '<path d="m15 18-6-6 6-6"/>',
    chevR: '<path d="m9 18 6-6-6-6"/>',
    chevD: '<path d="m6 9 6 6 6-6"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    moon: '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
    pause: '<rect x="6" y="5" width="4" height="14" rx="1"/><rect x="14" y="5" width="4" height="14" rx="1"/>',
    play: '<path d="m7 4 13 8-13 8z"/>',
    panel: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9 4v16"/>',
    more: '<circle cx="5" cy="12" r="1.5" fill="currentColor"/><circle cx="12" cy="12" r="1.5" fill="currentColor"/><circle cx="19" cy="12" r="1.5" fill="currentColor"/>',
    check: '<path d="m5 12 5 5L20 7"/>',
    refresh: '<path d="M21 12a9 9 0 1 1-3-6.7L21 8"/><path d="M21 3v5h-5"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    cal: '<rect x="3" y="4" width="18" height="17" rx="2"/><path d="M3 9h18M8 2v4M16 2v4"/>',
    x: '<path d="M18 6 6 18M6 6l12 12"/>',
    star: '<path d="m12 3 2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z"/>',
    warn: '<path d="M12 3 2 20h20z"/><path d="M12 10v4M12 17.5v.5"/>',
    home: '<path d="m3 11 9-8 9 8"/><path d="M5 10v10h14V10"/>',
  };
  const icon = (name, cls) => `<svg class="ico ${cls || ""}" viewBox="0 0 24 24" aria-hidden="true" focusable="false">${ICONS[name] || ""}</svg>`;

  const state = { weekOffset: 0, paused: false, hash: "" };
  const listeners = [];
  const emit = () => listeners.forEach((fn) => fn());

  /* ---- Dates (semaine factice ancrée sur le lundi 19 octobre 2026) ---- */
  const BASE_MONDAY = new Date(2026, 9, 19);
  const addDays = (d, n) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);
  const monday = () => addDays(BASE_MONDAY, state.weekOffset * 7);
  const fmt = (d, o) => d.toLocaleDateString("fr-FR", o);
  const DAYS = ["Lun", "Mar", "Mer", "Jeu", "Ven"];
  const isoWeek = (d) => {
    const t = new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()));
    t.setUTCDate(t.getUTCDate() + 4 - (t.getUTCDay() || 7));
    return Math.ceil(((t - Date.UTC(t.getUTCFullYear(), 0, 1)) / 864e5 + 1) / 7);
  };
  const weekRange = () => {
    const m = monday(), f = addDays(m, 4);
    return `${m.getDate()} au ${f.getDate()} ${fmt(f, { month: "long" })} ${f.getFullYear()}`.replace(/^(\d+) au (\d+) (\S+)/, (s, a, b, mo) =>
      m.getMonth() === f.getMonth() ? s : `${a} ${fmt(m, { month: "long" })} au ${b} ${mo}`);
  };
  const weekTitle = () => `Semaine du ${weekRange()}`;
  const weekNumber = () => isoWeek(monday());
  const weekRelative = () => (state.weekOffset === 0 ? "cette semaine" : state.weekOffset === -1 ? "semaine dernière" : `S${weekNumber()}`);
  const h = (n) => (n === 0 ? "–" : n.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 }));
  const hh = (n) => n.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + " h";

  /* ---- Données factices ---- */
  const TASKS = [
    { id: "dev", project: "ACME Portail", name: "Développement", color: "#0969da", hours: [4.5, 3.75, 5.25, 4, 2.5] },
    { id: "reu", project: "ACME Portail", name: "Réunions", color: "#8250df", hours: [1, 1.5, 0.75, 1, 1] },
    { id: "inc", project: "Support N3", name: "Incidents", color: "#cf222e", hours: [1.25, 0, 1.5, 2, 1.25] },
    { id: "for", project: "Interne", name: "Formation", color: "#1a7f37", hours: [0, 1, 0, 0.5, 0] },
  ];
  const UNCLASSIFIED = [0.75, 0.5, 0, 0.25, 1.75];
  const GOAL = 7.5;
  const sum = (a) => a.reduce((x, y) => x + y, 0);
  const dayTotals = () => DAYS.map((_, i) => sum(TASKS.map((t) => t.hours[i])));
  const classifiedTotal = () => sum(dayTotals());
  const unclassifiedTotal = () => sum(UNCLASSIFIED);
  const GAPS = 3;

  const weekTable = (withActions) => {
    const dt = dayTotals();
    const head = `<tr><th scope="col">Tâche</th>${DAYS.map((d, i) => `<th scope="col" class="num ${i === 2 && state.weekOffset === 0 ? "today" : ""}">${d} ${addDays(monday(), i).getDate()}</th>`).join("")}<th scope="col" class="num">Total</th></tr>`;
    const rows = TASKS.map((t) => `<tr><th scope="row"><span class="task-chip" style="background:${t.color}"></span>${t.project} <span class="muted">/ ${t.name}</span></th>${t.hours.map((v) => `<td class="num ${v ? "" : "cell-zero"}">${h(v)}</td>`).join("")}<td class="num"><strong>${h(sum(t.hours))}</strong></td></tr>`).join("");
    const unc = `<tr><th scope="row"><span class="badge warn">Non classé</span></th>${UNCLASSIFIED.map((v) => `<td class="num ${v ? "" : "cell-zero"}">${h(v)}</td>`).join("")}<td class="num"><strong>${h(unclassifiedTotal())}</strong></td></tr>`;
    const tot = `<tr class="total"><th scope="row">Total classé</th>${dt.map((v) => `<td class="num">${h(v)}</td>`).join("")}<td class="num">${h(classifiedTotal())}</td></tr>`;
    const goal = `<tr><th scope="row" class="muted">Objectif</th>${DAYS.map(() => `<td class="num muted">${h(GOAL)}</td>`).join("")}<td class="num muted">${h(GOAL * 5)}</td></tr>`;
    return `<div class="table-wrap"><table><thead>${head}</thead><tbody>${rows}${withActions ? unc : ""}${tot}${goal}</tbody></table></div>`;
  };

  const SLOTS = [
    ["08:00", 3, "activity", "Visual Studio — TimeSheetHelper.sln", "ACME Portail / Développement"],
    ["08:45", 1, "unclassified", "Teams — Conversation", "Non classé"],
    ["09:00", 2, "meeting", "Réunion — Point quotidien équipe", "ACME Portail / Réunions"],
    ["09:30", 6, "activity", "Visual Studio — WeekProposalBuilder.cs", "ACME Portail / Développement"],
    ["11:00", 3, "activity", "Azure DevOps — Bogue #4821", "Support N3 / Incidents"],
    ["11:45", 1, "unclassified", "Edge — Documentation interne", "Non classé"],
    ["12:00", 4, "excluded", "Pause dîner (verrouillé)", "Exclu"],
    ["13:00", 2, "empty", "Activité trop brève (non comptée)", "—"],
    ["13:30", 5, "activity", "VS Code — rules.json", "ACME Portail / Développement"],
    ["14:45", 4, "meeting", "Réunion — Revue de sprint", "ACME Portail / Réunions"],
    ["15:45", 3, "activity", "Azure DevOps — Bogue #4829", "Support N3 / Incidents"],
  ];
  const slotRows = () => {
    const out = [];
    SLOTS.forEach(([start, n, kind, label, task]) => {
      let [hh0, mm] = start.split(":").map(Number);
      for (let i = 0; i < n; i++) {
        const t = `${String(hh0).padStart(2, "0")}:${String(mm).padStart(2, "0")}`;
        out.push(`<div class="slot k-${kind}"><span class="num muted">${t}</span><span class="band"></span><span>${label}</span><span class="muted">${task}</span></div>`);
        mm += 15; if (mm >= 60) { mm -= 60; hh0++; }
      }
    });
    return out.join("");
  };

  const gapList = () => `<div class="card"><div class="card-head"><h3>Trous à combler</h3><span class="badge warn">${GAPS} à traiter</span></div>
    <table><tbody>
      <tr><td>Mardi 20 octobre · 14:00 – 15:15</td><td class="muted">Aucune activité détectée</td><td class="num"><button class="btn" data-action="toast" data-msg="Assignation… (prototype)">Assigner</button></td></tr>
      <tr><td>Mercredi 21 octobre · 10:30 – 11:00</td><td class="muted">Activité trop brève</td><td class="num"><button class="btn" data-action="toast" data-msg="Assignation… (prototype)">Assigner</button></td></tr>
      <tr><td>Vendredi 23 octobre · 16:00 – 17:30</td><td class="muted">Non classé</td><td class="num"><button class="btn" data-action="toast" data-msg="Assignation… (prototype)">Assigner</button></td></tr>
    </tbody></table></div>`;

  const sw = (on, label) => `<label class="switch"><input type="checkbox" ${on ? "checked" : ""} aria-label="${label}"><span>${label}</span></label>`;

  /* ---- Pages (mêmes noms/routes que l'application) ---- */
  const A = {
    prev: { id: "prev-week", label: "Semaine précédente", icon: "chevL", iconOnly: true },
    next: { id: "next-week", label: "Semaine suivante", icon: "chevR", iconOnly: true },
  };
  const PAGES = [
    {
      id: "semaine", label: "Semaine", route: "/", icon: "semaine", group: "temps", key: "1", keywords: "semaine calendrier heures grille",
      desc: "Grille hebdomadaire des heures par tâche",
      title: () => weekTitle(),
      actions: [{ id: "recompute", label: "Recalculer", icon: "refresh" }],
      body: () => `<div class="cols" style="margin-bottom:14px">
          <div class="card stat"><span class="muted">Heures classées</span><span class="v">${hh(classifiedTotal())}</span><span class="muted">sur ${hh(GOAL * 5)} (objectif)</span></div>
          <div class="card stat"><span class="muted">Non classé</span><span class="v">${hh(unclassifiedTotal())}</span><span class="badge warn" style="align-self:flex-start">À classer</span></div>
          <div class="card stat"><span class="muted">Surveillance</span><span class="v">${state.paused ? "En pause" : "Active"}</span><span class="badge ${state.paused ? "warn" : "ok"}" style="align-self:flex-start"><span class="dot"></span>${state.paused ? "Aucune capture" : "Capture en cours"}</span></div></div>
        ${weekTable(true)}
        <h2 id="unclassified-title">Activité non classée (${hh(unclassifiedTotal())})</h2>
        <div class="alert warn"><span class="body">Ces heures n'ont pas de tâche Forecast. Créez une règle ou assignez-les depuis la page <strong>Jour</strong>.</span></div>
        <div class="table-wrap"><table><thead><tr><th>Quand</th><th>Application</th><th>Titre</th><th class="num">Durée</th></tr></thead><tbody>
          <tr><td>Lun 19 · 08:45</td><td>Teams</td><td>Conversation — Équipe Portail</td><td class="num">0,25 h</td></tr>
          <tr><td>Mar 20 · 15:10</td><td>Edge</td><td>Wiki — Procédure de déploiement</td><td class="num">0,50 h</td></tr>
          <tr><td>Ven 23 · 16:00</td><td>Outlook</td><td>Boîte de réception</td><td class="num">1,75 h</td></tr></tbody></table></div>`,
    },
    {
      id: "jour", label: "Jour", route: "/jour", icon: "jour", group: "temps", key: "2", keywords: "jour journée créneaux 15 minutes",
      desc: "Créneaux de 15 minutes d'une journée",
      title: () => { const d = addDays(monday(), 2); const s = fmt(d, { weekday: "long", day: "numeric", month: "long", year: "numeric" }); return s.charAt(0).toUpperCase() + s.slice(1); },
      actions: [{ id: "recompute", label: "Recalculer", icon: "refresh" }],
      body: () => `<div class="toolbar-inline"><span class="badge ok">${hh(dayTotals()[2])} classées</span><span class="badge warn">${hh(UNCLASSIFIED[2] + 0.5)} non classées</span><span class="muted">Objectif ${hh(GOAL)}</span></div>
        <h2 style="margin-top:0">Créneaux de 15 minutes</h2><div class="slots">${slotRows()}</div>`,
    },
    {
      id: "soumettre", label: "Soumettre", route: "/soumettre", icon: "soumettre", group: "envoi", key: "3", keywords: "soumettre forecast envoyer brouillon trous",
      desc: "Vérifier le brouillon puis envoyer à Forecast", badge: { text: String(GAPS), tone: "warn", label: `${GAPS} trous à combler` },
      title: () => "Soumettre à Forecast",
      actions: [{ id: "recompute", label: "Recalculer", icon: "refresh" }, { id: "submit", label: "Soumettre à Forecast", primary: true, icon: "soumettre" }],
      body: () => `<div class="alert warn"><span class="body"><strong>Brouillon incomplet :</strong> ${GAPS} trous et ${hh(unclassifiedTotal())} non classées. Comblez-les ou ignorez-les avant d'envoyer.</span></div>
        <div class="toolbar-inline"><span class="badge info">Brouillon — ${hh(classifiedTotal())}</span><span class="badge accent">Synchronisé avec Forecast il y a 12 min</span></div>
        ${weekTable(false)}${gapList()}
        <div class="toolbar-inline"><button class="btn link" data-action="toast" data-msg="Semaine sélectionnée (prototype)">Sélectionner toute la semaine</button><button class="btn link" data-action="toast" data-msg="Choix réinitialisés (prototype)">Réinitialiser les choix de la semaine</button></div>`,
    },
    {
      id: "regles", label: "Règles", route: "/regles", icon: "regles", group: "config", key: "4", keywords: "règles conditions classification automatique",
      desc: "Règles de classification automatique",
      title: () => "Règles",
      actions: [{ id: "new-rule", label: "Nouvelle règle", primary: true, icon: "plus" }],
      body: () => `<div class="table-wrap"><table><thead><tr><th class="num">#</th><th>Nom</th><th>Condition</th><th>Tâche Forecast</th><th>Origine</th><th>Active</th><th></th></tr></thead><tbody>
        ${[
          ["Visual Studio · TimeSheetHelper", "Application = devenv.exe ET titre contient « TimeSheetHelper »", "ACME Portail / Développement", "Manuelle", 1],
          ["Réunions Teams", "Source = Microsoft 365 ET type = Réunion", "ACME Portail / Réunions", "Manuelle", 1],
          ["Bogues Azure DevOps", "Source = DevOps ET étiquette contient « support »", "Support N3 / Incidents", "Suggérée", 1],
          ["Formation Pluralsight", "Domaine = app.pluralsight.com", "Interne / Formation", "Manuelle", 0],
        ].map((r, i) => `<tr><td class="num">${i + 1}</td><td>${r[0]}</td><td class="muted">${r[1]}</td><td>${r[2]}</td><td><span class="badge ${r[3] === "Suggérée" ? "accent" : "info"}">${r[3]}</span></td><td>${sw(r[4], "Active")}</td><td class="num"><button class="btn icon" aria-label="Monter">↑</button> <button class="btn icon" aria-label="Descendre">↓</button> <button class="btn">Modifier</button></td></tr>`).join("")}
        </tbody></table></div>
        <div class="card"><div class="card-head"><h3>Aperçu — ${weekTitle()}</h3></div><p class="muted" style="margin:0">La règle sélectionnée correspond à 14 segments, soit 11,25 h.</p></div>`,
    },
    {
      id: "taches", label: "Tâches", route: "/taches", icon: "taches", group: "config", key: "5", keywords: "tâches forecast projets favoris",
      desc: "Tâches Forecast connues (projets, favoris)",
      title: () => "Tâches Forecast",
      actions: [{ id: "new-task", label: "Ajouter une tâche", primary: true, icon: "plus" }],
      body: () => `<div class="toolbar-inline"><input type="search" placeholder="Filtrer les tâches…" aria-label="Filtrer les tâches"><span class="muted">7 tâches · 5 actives</span><span class="spacer"></span><button class="btn">Activer</button><button class="btn">Désactiver</button><button class="btn danger-outline">Supprimer</button></div>
        <div class="table-wrap"><table><thead><tr><th><input type="checkbox" aria-label="Tout sélectionner"></th><th></th><th>Projet</th><th>Tâche</th><th>Vue le</th><th></th></tr></thead><tbody>
        ${[
          ["ACME Portail", "Développement", "19 oct. 2026", 1],
          ["ACME Portail", "Réunions", "19 oct. 2026", 1],
          ["ACME Portail", "Revue de code", "16 oct. 2026", 0],
          ["Support N3", "Incidents", "19 oct. 2026", 1],
          ["Support N3", "Astreinte", "02 oct. 2026", 0],
          ["Interne", "Formation", "15 oct. 2026", 0],
          ["Interne", "Administration", "09 oct. 2026", 0],
        ].map((r) => `<tr><td><input type="checkbox" aria-label="Sélectionner ${r[1]}"></td><td style="color:${r[3] ? "var(--warn)" : "var(--muted)"}">${icon("star", "sm")}</td><td>${r[0]}</td><td>${r[1]}</td><td class="muted">${r[2]}</td><td class="num"><button class="btn">Modifier</button></td></tr>`).join("")}
        </tbody></table></div>`,
    },
    {
      id: "sources", label: "Sources", route: "/sources", icon: "sources", group: "config", key: "6", keywords: "sources plugins collecteurs forecast connexion microsoft devops",
      desc: "Plugins de collecte et connexion Forecast", badge: { text: "1", tone: "warn", label: "1 source demande une action" },
      title: () => "Sources",
      actions: [{ id: "install-plugin", label: "Installer un plugin", icon: "plus" }],
      body: () => `<h2 style="margin-top:0">Sources</h2><div class="cols">
          <div class="card"><div class="card-head"><h3>Activité Windows <code>focus-tracker</code></h3></div><p class="muted">Fenêtre active, dossier et fichier ouverts.</p>${sw(true, "Activée")} <span class="badge ${state.paused ? "warn" : "ok"}"><span class="dot"></span>${state.paused ? "En pause" : "En cours"}</span></div>
          <div class="card"><div class="card-head"><h3>Azure DevOps <code>devops</code></h3></div><p class="muted">Éléments de travail, validations et demandes de tirage.</p>${sw(true, "Activée")} <span class="badge ok"><span class="dot"></span>Connectée</span></div>
          <div class="card"><div class="card-head"><h3>Microsoft 365 <code>microsoft-365</code></h3></div><p class="muted">Calendrier et réunions Teams.</p>${sw(true, "Activée")} <span class="badge warn"><span class="dot"></span>Reconnexion requise</span> <button class="btn" style="margin-top:8px">Se reconnecter</button></div></div>
        <h2>Forecast</h2><div class="card"><div class="card-head"><h3>Feuille de temps Forecast</h3><span class="badge ok"><span class="dot"></span>Connecté</span></div><p class="muted">Dernière synchronisation des tâches : aujourd'hui, 08:12.</p><div class="toolbar-inline" style="margin:0"><button class="btn primary">Se connecter à Forecast</button><button class="btn">Synchroniser les tâches</button><button class="btn">Se déconnecter</button></div></div>`,
    },
    {
      id: "activite", label: "Activité brute", route: "/activite", icon: "activite", group: "avance", key: "7", keywords: "activité brute journal segments diagnostic",
      desc: "Journal brut de la capture du jour",
      title: () => "Activité du jour",
      actions: [{ id: "refresh", label: "Rafraîchir", icon: "refresh" }, { id: "toggle-pause", label: "pause", icon: "pause", dynamic: true }],
      body: () => `<h2 style="margin-top:0">Totaux (6,75 h actif)</h2><div class="table-wrap"><table><thead><tr><th>Application</th><th>Dossier</th><th class="num">Durée</th></tr></thead><tbody>
          <tr><td>devenv.exe</td><td><code>C:\\git\\time-sheet-helper</code></td><td class="num">3 h 45</td></tr>
          <tr><td>ms-teams.exe</td><td>—</td><td class="num">1 h 15</td></tr>
          <tr><td>msedge.exe</td><td>—</td><td class="num">1 h 00</td></tr>
          <tr><td>Code.exe</td><td><code>C:\\git\\rules</code></td><td class="num">0 h 45</td></tr></tbody></table></div>
        <h2>Segments</h2><div class="table-wrap"><table><thead><tr><th>Début</th><th>Fin</th><th class="num">Durée</th><th>Source</th><th>Application</th><th>Titre</th></tr></thead><tbody>
          <tr><td>08:00</td><td>08:44</td><td class="num">0:44</td><td>focus-tracker</td><td>devenv.exe</td><td>TimeSheetHelper — Visual Studio</td></tr>
          <tr><td>08:44</td><td>09:00</td><td class="num">0:16</td><td>focus-tracker</td><td>ms-teams.exe</td><td>Conversation</td></tr>
          <tr class="idle"><td>12:00</td><td>13:00</td><td class="num">1:00</td><td>focus-tracker</td><td colspan="2" class="muted">Inactif — session verrouillée</td></tr>
          <tr><td>13:30</td><td>14:45</td><td class="num">1:15</td><td>focus-tracker</td><td>Code.exe</td><td>rules.json — rules</td></tr></tbody></table></div>`,
    },
  ];
  const GROUPS = [
    { id: "temps", label: "Ma semaine" },
    { id: "envoi", label: "Envoi" },
    { id: "config", label: "Configuration" },
    { id: "avance", label: "Avancé" },
  ];
  const byId = (id) => PAGES.find((p) => p.id === id) || PAGES[0];

  /* ---- Actions & toasts ---- */
  let toastEl, toastTimer;
  function toast(msg) {
    if (!toastEl) {
      toastEl = document.createElement("div");
      toastEl.setAttribute("role", "status");
      toastEl.style.cssText = "position:fixed;left:50%;bottom:24px;transform:translateX(-50%);z-index:300;background:var(--fg);color:var(--bg);padding:8px 16px;border-radius:8px;box-shadow:var(--shadow);font-size:.9rem;max-width:90vw";
      document.body.appendChild(toastEl);
    }
    toastEl.textContent = msg;
    toastEl.style.display = "block";
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => (toastEl.style.display = "none"), 2200);
  }
  function runAction(id, el) {
    switch (id) {
      case "prev-week": state.weekOffset--; emit(); break;
      case "next-week": state.weekOffset++; emit(); break;
      case "this-week": state.weekOffset = 0; emit(); break;
      case "toggle-pause": state.paused = !state.paused; emit(); toast(state.paused ? "Surveillance en pause" : "Surveillance reprise"); break;
      case "toggle-theme": setTheme(effectiveTheme() === "dark" ? "light" : "dark"); break;
      case "recompute": toast("Semaine recalculée (prototype)"); break;
      case "submit": toast("Soumission à Forecast… (prototype)"); break;
      case "toast": toast((el && el.dataset.msg) || "Action simulée (prototype)"); break;
      default: toast("Action simulée : " + id);
    }
  }
  document.addEventListener("click", (e) => {
    const t = e.target.closest("[data-action]");
    if (t) { e.preventDefault(); runAction(t.dataset.action, t); }
  });
  const actionLabel = (a) => (a.dynamic ? (state.paused ? "Reprendre la surveillance" : "Mettre en pause") : a.label);
  const actionIcon = (a) => (a.dynamic ? (state.paused ? "play" : "pause") : a.icon);
  const actionButton = (a) =>
    `<button type="button" class="btn ${a.primary ? "primary" : ""} ${a.iconOnly ? "icon" : ""}" data-action="${a.id}" ${a.iconOnly ? `aria-label="${a.label}" title="${a.label}"` : ""}>${a.icon ? icon(actionIcon(a), "sm") : ""}${a.iconOnly ? "" : `<span>${actionLabel(a)}</span>`}</button>`;
  const weekNavHtml = () => `<div class="week-nav" role="group" aria-label="Navigation de semaine"><button type="button" class="btn icon" data-action="prev-week" aria-label="Semaine précédente" title="Semaine précédente ( [ )">${icon("chevL", "sm")}</button><button type="button" class="btn" data-action="this-week" ${state.weekOffset === 0 ? 'aria-current="date"' : ""}>${weekRelative()}</button><button type="button" class="btn icon" data-action="next-week" aria-label="Semaine suivante" title="Semaine suivante ( ] )">${icon("chevR", "sm")}</button></div>`;

  /* ---- Thème ---- */
  const KEY = "tsh-proto-theme";
  const effectiveTheme = () => document.documentElement.dataset.theme || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  function setTheme(t) {
    document.documentElement.dataset.theme = t;
    try { localStorage.setItem(KEY, t); } catch (_) { /* file:// restreint */ }
    emit();
  }
  (function initTheme() {
    const q = new URLSearchParams(location.search).get("theme");
    let t = q;
    if (!t) { try { t = localStorage.getItem(KEY); } catch (_) { t = null; } }
    if (t === "dark" || t === "light") document.documentElement.dataset.theme = t;
  })();

  /* ---- Routage par hash (#/semaine) ---- */
  const current = () => byId((location.hash.replace(/^#\/?/, "") || "semaine").split("?")[0]);
  const go = (id) => { if (location.hash !== "#/" + id) location.hash = "#/" + id; else emit(); };
  window.addEventListener("hashchange", () => { emit(); });
  /* Raccourcis communs : Alt+1..7 = pages, [ et ] = semaine précédente/suivante, Alt+T = thème. */
  document.addEventListener("keydown", (e) => {
    const typing = /^(INPUT|TEXTAREA|SELECT)$/.test((e.target.tagName || "")) || e.target.isContentEditable;
    if (e.altKey && !e.ctrlKey && !e.metaKey) {
      const p = PAGES.find((x) => x.key === e.key);
      if (p) { e.preventDefault(); go(p.id); return; }
      if (e.key.toLowerCase() === "t") { e.preventDefault(); runAction("toggle-theme"); return; }
    }
    if (!typing && !e.ctrlKey && !e.metaKey && !e.altKey) {
      if (e.key === "[") runAction("prev-week");
      else if (e.key === "]") runAction("next-week");
    }
  });

  window.TSH = {
    icon, ICONS, PAGES, GROUPS, state, byId, current, go, on: (fn) => listeners.push(fn), emit,
    weekTitle, weekRange, weekNumber, weekRelative, weekNavHtml, actionButton, actionLabel, actionIcon, runAction, toast,
    effectiveTheme, setTheme, hh, h, DAYS, monday, addDays, fmt,
    classifiedTotal, unclassifiedTotal, dayTotals, GOAL, GAPS, TASKS,
  };
})();
