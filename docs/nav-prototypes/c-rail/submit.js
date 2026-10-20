/* Prototype C+ — Soumission jour par jour (maquette, aucune donnée réelle).
   Chargé après ../shared/pages.js. Remplace le corps de la page « Soumettre » et expose window.TSHS
   (bande de jours, statuts) utilisé par c-rail/index.html. */
(function () {
  "use strict";
  const T = window.TSH;
  const TASKS = T.TASKS, GOAL = T.GOAL, EPS = 0.01, TODAY = 2;
  const DAYNAMES = ["lun.", "mar.", "mer.", "jeu.", "ven."];
  const eq = (a, b) => Math.abs((a || 0) - (b || 0)) < EPS;
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const pad = (n) => String(n).padStart(2, "0");
  const cap = (s) => s.charAt(0).toUpperCase() + s.slice(1);
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const t0 = Date.now();
  const clock = () => { const s = 14 * 3600 + 32 * 60 + 7 + Math.floor((Date.now() - t0) / 1000); return `${pad(Math.floor(s / 3600) % 24)}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}`; };
  const stamp = (i) => { const d = T.addDays(T.monday(), i); return `${DAYNAMES[i]} ${d.getDate()} ${T.fmt(d, { month: "short" })} ${clock()}`; };
  const dayLabel = (i) => `${DAYNAMES[i]} ${T.addDays(T.monday(), i).getDate()}`;
  const live = () => T.state.weekOffset === 0;
  const tag = (k) => k === "exist" ? '<span class="tag exist" title="Existe déjà dans l\'application">Existe</span>' : k === "partial" ? '<span class="tag partial" title="Existe pour la semaine, pas par jour">Existe (semaine)</span>' : '<span class="tag new" title="À construire">À construire</span>';

  /* Heures du brouillon par tâche, lun → ven (jeu/ven = à venir). */
  const BASE = { dev: [4.5, 4.25, 5.25, 0, 0], reu: [1, 1.5, 0.75, 0, 0], inc: [1.5, 0.75, 1.5, 0, 0], for: [0.5, 1, 0, 0, 0] };
  const UI = { scen: false, legend: false, guards: false, hist: true, histAll: false };

  const SCENARIOS = [
    { id: "neuf", label: "Jour neuf", desc: "Mer. 21 n'a jamais été envoyé : tout est nouveau. Mar. 20 aussi est prêt (2 jours prêts). « Formation » est ignorée pour la semaine." },
    { id: "inchange", label: "Jour envoyé, inchangé", desc: "Lun. à mer. déjà envoyés et identiques à Forecast : rien à écrire, le bouton est désactivé." },
    { id: "modifie", label: "Modifié après l'envoi", desc: "Mer. 21 a été envoyé puis le brouillon a changé : deux heures modifiées, une tâche supprimée localement, une nouvelle tâche." },
    { id: "conflit", label: "Conflit Forecast", desc: "Forecast contient 4,00 h pour « Développement » (saisi ailleurs) alors que le brouillon dit 5,25 h. Une tâche est verrouillée (approuvée)." },
    { id: "partielle", label: "Erreur partielle", desc: "L'envoi de mer. 21 a été interrompu : 2 cellules sur 4 sont écrites. Il faut relire Forecast avant de rejouer." },
    { id: "double", label: "Double clic", desc: "Mer. 21 est neuf. Utilisez « Simuler un double clic » : le second clic est refusé par le verrou, aucun doublon." },
  ];

  let M;

  const hash = (s) => { let h = 5381; for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) >>> 0; return h.toString(16).padStart(8, "0"); };
  const fp = (i, field) => hash(`S${T.weekNumber()}|` + TASKS.filter((t) => !M.ignored[t.id]).map((t) => `${t.id}@${i}=${((M.days[i].cells[t.id][field]) || 0).toFixed(2)}`).join("|")).slice(0, 6);

  function load(id) {
    const days = [0, 1, 2, 3, 4].map((i) => {
      const cells = {};
      TASKS.forEach((t) => { cells[t.id] = { D: BASE[t.id][i], S: null, F: null, w: false }; });
      return { cells, locked: {}, sentAt: null, sentFp: null, log: [], partial: false, noproject: 0 };
    });
    M = { scenario: id, days, sel: null, ignored: {}, opts: { update: true }, res: {}, busy: null, notice: null, stale: false, readAt: "14:30:41", cancel: false, inject: false, confirming: false, busyHits: 0 };
    const seed = (i, when) => {
      const d = days[i]; let n = 0;
      TASKS.forEach((t) => { const c = d.cells[t.id]; if (M.ignored[t.id] || !c.D) return; c.S = c.F = c.D; n++; });
      d.sentAt = when; d.sentFp = fp(i, "S");
      d.log.push({ t: when, a: "Soumettre ce jour", r: "ok", x: `${n} cellule${n > 1 ? "s" : ""} écrite${n > 1 ? "s" : ""} et vérifiée${n > 1 ? "s" : ""}`, fp: d.sentFp });
    };
    const WHEN = ["lun. 19 oct. 17:48:12", "mar. 20 oct. 17:41:03", "mer. 21 oct. 11:02:45"];
    const set = (i, id2, v) => { days[i].cells[id2].D = v; };
    if (id === "neuf") {
      M.ignored.for = true; set(2, "dev", 4.75); set(2, "for", 0.5); days[2].noproject = 0.5;
      seed(0, WHEN[0]);
    } else if (id === "inchange") {
      seed(0, WHEN[0]); seed(1, WHEN[1]); seed(2, WHEN[2]);
    } else if (id === "modifie") {
      seed(0, WHEN[0]); seed(1, WHEN[1]); seed(2, WHEN[2]);
      set(2, "dev", 5.75); set(2, "reu", 0.25); set(2, "inc", 0); set(2, "for", 1.5);
    } else if (id === "conflit") {
      seed(0, WHEN[0]); seed(1, WHEN[1]);
      set(2, "inc", 1); set(2, "for", 0.5);
      days[2].cells.dev.F = 4; days[2].locked.for = true;
      days[2].log.push({ t: "mer. 21 oct. 14:32:06", a: "Simulation", r: "warn", x: "1 conflit : Forecast contient 4,00 h pour « Développement »", fp: fp(2, "D") });
    } else if (id === "partielle") {
      seed(0, WHEN[0]); seed(1, WHEN[1]);
      const d = days[2];
      ["dev", "reu"].forEach((k) => { d.cells[k].S = d.cells[k].F = d.cells[k].D; d.cells[k].w = true; });
      d.partial = true; M.stale = true;
      d.log.push({ t: "mer. 21 oct. 14:31:48", a: "Soumettre ce jour", r: "danger", x: "Interrompu après 2/4 cellules — connexion Forecast perdue", fp: fp(2, "D") });
    } else {
      seed(0, WHEN[0]); seed(1, WHEN[1]);
    }
  }

  /* ---- Calculs ---- */
  const totalD = (i) => TASKS.reduce((s, t) => s + (M.days[i].cells[t.id].D || 0), 0);

  function cellState(i, t) {
    const d = M.days[i], c = d.cells[t.id], D = c.D || 0, hasF = c.F != null, hasS = c.S != null;
    if (!D && !hasF && !hasS) return "empty";
    if (M.ignored[t.id]) return "ignored";
    if (d.locked[t.id]) return "locked";
    if (D && !hasF) return d.partial ? "notattempted" : "new";
    if (D && hasF) return eq(D, c.F) ? "same" : hasS && eq(c.F, c.S) ? "changed" : "conflict";
    return hasS ? "deleted" : "conflict";
  }

  function plan(i) {
    const d = M.days[i], rows = [], ops = [];
    TASKS.forEach((t) => {
      const st = cellState(i, t);
      if (st === "empty") return;
      const c = d.cells[t.id], r = M.res[i + ":" + t.id] || "keep";
      let op = null;
      if (st === "new" || st === "notattempted") op = { k: "write", h: c.D };
      else if (st === "changed" && M.opts.update) op = { k: "update", h: c.D };
      else if (st === "conflict" || st === "deleted") {
        if (r === "accept") op = { k: "accept" };
        else if (r === "complement" && c.D > c.F) op = { k: "complement", h: c.D - c.F, risky: true };
        else if (r === "remove" && st === "deleted") op = { k: "delete", risky: true };
      }
      if (op) { op.t = t; ops.push(op); }
      rows.push({ t, c, st, op });
    });
    const count = (f) => rows.filter(f).length;
    const counts = {
      write: ops.filter((o) => o.k !== "accept").length,
      already: count((r) => r.st === "same"),
      ignored: count((r) => r.st === "ignored"),
      notSent: count((r) => ["locked", "conflict", "deleted", "changed"].includes(r.st) && !r.op) + (d.noproject ? 1 : 0),
    };
    return { rows, ops, risky: ops.filter((o) => o.risky), counts, unresolved: rows.filter((r) => r.st === "conflict" && !r.op) };
  }

  function status(i) {
    const none = { key: "none", label: "Non saisi", tone: "none", icon: null };
    if (!live()) return none;
    const d = M.days[i], D = totalD(i);
    const anyF = TASKS.some((t) => d.cells[t.id].F != null);
    if (!D && !anyF) return none;
    const p = plan(i);
    if (d.partial || p.unresolved.length) return { key: "gap", label: d.partial ? "Envoi partiel" : "Écart", tone: "gap", icon: "warn" };
    if (!eq(D, GOAL)) return i === TODAY && D < GOAL ? { key: "draft", label: "Brouillon", tone: "draft", icon: "jour" } : { key: "gap", label: "Écart", tone: "gap", icon: "warn" };
    if (p.rows.some((r) => r.st === "changed" || r.st === "deleted")) return { key: "modified", label: "Modifié depuis l'envoi", tone: "modified", icon: "warn" };
    if (p.rows.some((r) => r.st === "new")) return { key: "ready", label: "Prêt", tone: "ready", icon: "soumettre" };
    return { key: "sent", label: "Envoyé", tone: "sent", icon: "check" };
  }

  function gate(i) {
    const p = plan(i), d = M.days[i], D = totalD(i);
    if (M.busy) return { ok: false, aria: true, label: `Envoi en cours… ${M.busy.done}/${M.busy.total}`, reason: "Verrou Forecast actif : un second clic est ignoré." };
    if (!live() || (!D && !p.rows.length)) return { ok: false, label: "Rien à envoyer", reason: "Aucune donnée pour ce jour." };
    if (p.unresolved.length) return { ok: false, label: "Écart à résoudre", reason: "Choisissez comment traiter chaque conflit ci-dessous : l'application n'écrase jamais Forecast." };
    if (!eq(D, GOAL)) return { ok: false, label: "Écart d'heures", reason: `Le jour compte ${T.hh(D)} pour un objectif de ${T.hh(GOAL)} : corrigez ou classez le temps manquant.` };
    if (d.partial && M.stale) return { ok: false, label: "Relire Forecast d'abord", reason: "La simulation est périmée : relisez Forecast avant de rejouer (risque de doublon)." };
    if (!p.ops.length) return { ok: false, label: "Rien de nouveau à envoyer", reason: p.counts.notSent ? `${p.counts.notSent} cellule(s) restent non envoyées (voir le tableau).` : "Tout est déjà dans Forecast." };
    const writes = p.counts.write;
    const label = d.partial ? "Rejouer (écrit seulement ce qui manque)" : !writes ? "Appliquer (sans écriture Forecast)" : d.sentAt ? "Envoyer les changements" : "Soumettre ce jour";
    return { ok: true, label, reason: `${writes} écriture${writes > 1 ? "s" : ""} prévue${writes > 1 ? "s" : ""}${p.risky.length ? ` · ${p.risky.length} opération(s) à confirmer` : ""}.` };
  }

  const selDay = () => {
    if (M.sel != null) return M.sel;
    if (live()) return TODAY;
    return 0;
  };
  const readyDays = () => [0, 1, 2, 3, 4].filter((i) => status(i).key === "ready" && gate(i).ok);
  const todoCount = () => (live() ? [0, 1, 2, 3, 4].filter((i) => ["ready", "modified", "gap"].includes(status(i).key)).length : 0);

  /* ---- Journal ---- */
  const log = (i, a, r, x) => { const d = M.days[i]; d.log.push({ t: stamp(i), a, r, x, fp: d.sentFp || fp(i, "D") }); };

  /* ---- Moteur d'envoi simulé ---- */
  function refresh() {
    const a = document.activeElement, fk = a && a.dataset && a.dataset.fk;
    T.emit();
    if (fk) { const n = document.querySelector(`[data-fk="${fk}"]`); if (n && !n.disabled) n.focus({ preventScroll: true }); }
  }

  function applyOp(i, op) {
    const c = M.days[i].cells[op.t.id];
    if (op.k === "write" || op.k === "update" || op.k === "complement") { c.F = c.S = c.D; c.w = true; }
    else if (op.k === "accept") { c.D = c.F || 0; c.S = c.F; }
    else if (op.k === "delete") { c.F = null; c.S = null; }
  }

  function confirmRisky(i, risky) {
    return new Promise((resolve) => {
      let dlg = document.getElementById("sub-confirm");
      if (!dlg) { dlg = document.createElement("dialog"); dlg.id = "sub-confirm"; dlg.className = "sub-dlg"; dlg.setAttribute("aria-labelledby", "sub-confirm-t"); document.body.appendChild(dlg); }
      const txt = { complement: (o) => `Ajouter un complément de <strong>${T.hh(o.h)}</strong> sur « ${esc(o.t.name)} » — Forecast contient déjà des heures sur ce jour.`, delete: (o) => `Supprimer dans Forecast les heures de « ${esc(o.t.name)} » (supprimées localement).` };
      dlg.innerHTML = `<form method="dialog"><h2 id="sub-confirm-t">Confirmer ${risky.length > 1 ? "ces opérations" : "cette opération"} ?</h2>
        <p class="muted">${esc(dayLabel(i))} — ces changements modifient des données déjà présentes dans Forecast.</p>
        <ul>${risky.map((o) => `<li>${txt[o.k](o)}</li>`).join("")}</ul>
        <div class="sub-dlg-actions"><button class="btn" value="cancel" autofocus>Annuler</button><button class="btn primary" value="ok">Confirmer et envoyer</button></div></form>`;
      dlg.addEventListener("close", function h() { dlg.removeEventListener("close", h); resolve(dlg.returnValue === "ok"); });
      dlg.returnValue = "cancel";
      dlg.showModal();
    });
  }

  async function send(list) {
    if (M.busy || M.confirming) {
      M.busyHits++;
      const msg = "Une opération Forecast est déjà en cours — clic ignoré, aucune écriture en double.";
      M.notice = { tone: "warn", text: msg };
      if (M.busy) log(M.busy.day, "Clic ignoré", "info", "Verrou Forecast actif (ForecastBusyException)");
      T.toast("Une opération Forecast est déjà en cours");
      refresh();
      return;
    }
    const queue = list.filter((i) => gate(i).ok);
    if (!queue.length) return;
    if (queue.length === 1) {
      const risky = plan(queue[0]).risky;
      if (risky.length) {
        M.confirming = true;
        const ok = await confirmRisky(queue[0], risky);
        M.confirming = false;
        if (!ok) { log(queue[0], "Confirmation refusée", "info", "Aucune écriture"); M.notice = { tone: "info", text: "Envoi annulé avant écriture : rien n'a été modifié dans Forecast." }; refresh(); return; }
      }
    }
    const total = queue.reduce((s, i) => s + plan(i).ops.length, 0);
    M.busy = { day: queue[0], done: 0, total }; M.cancel = false; M.notice = null;
    refresh();
    let done = 0, failed = null, cancelled = false;
    const results = [];
    for (const i of queue) {
      M.busy.day = i;
      const ops = plan(i).ops;
      let n = 0, w = 0;
      for (const op of ops) {
        await sleep(520);
        if (M.cancel) { cancelled = true; break; }
        if (M.inject && n === 2) { failed = i; break; }
        applyOp(i, op); n++; done++; if (op.k !== "accept") w++; M.busy.done = done; refresh();
      }
      const d = M.days[i];
      if (failed === i || cancelled) {
        d.partial = true; M.stale = true;
        log(i, "Soumettre ce jour", failed === i ? "danger" : "warn", failed === i ? `Interrompu après ${n}/${ops.length} cellules — connexion Forecast perdue` : `Annulé après ${n}/${ops.length} cellules (les cellules écrites restent dans Forecast)`);
        break;
      }
      d.partial = false; d.sentAt = stamp(i); d.sentFp = fp(i, "S");
      TASKS.forEach((t) => { d.cells[t.id].w = false; });
      log(i, d.log.some((l) => l.r === "ok") ? "Envoyer les changements" : "Soumettre ce jour", "ok", `${w} écriture${w > 1 ? "s" : ""} vérifiée${w > 1 ? "s" : ""}${ops.length - w ? ` · ${ops.length - w} sans écriture Forecast` : ""}`);
      results.push(dayLabel(i));
    }
    M.busy = null; M.inject = false;
    if (failed != null) M.notice = { tone: "danger", text: `Envoi interrompu (${dayLabel(failed)}) : certaines cellules sont écrites, d'autres non tentées. Relisez Forecast puis rejouez : seul ce qui manque sera écrit.`, role: "alert" };
    else if (cancelled) M.notice = { tone: "warn", text: "Envoi annulé. Les cellules déjà écrites restent dans Forecast (pas d'annulation côté Forecast). Relisez Forecast avant de rejouer." };
    else M.notice = { tone: "ok", text: `Envoyé et vérifié : ${results.join(", ")}. Un nouveau clic n'écrira rien de plus.` };
    refresh();
  }

  async function reread() {
    if (M.busy) { send([selDay()]); return; }
    M.busy = { day: selDay(), done: 0, total: 0, reading: true }; M.notice = null; refresh();
    await sleep(900);
    M.busy = null; M.stale = false; M.readAt = clock();
    log(selDay(), "Relire Forecast", "info", "Simulation rafraîchie (lecture seule)");
    M.notice = { tone: "info", text: `Forecast relu à ${M.readAt} : la simulation est à jour.` };
    refresh();
  }

  /* ---- Rendu : bande de jours (en-tête) ---- */
  const hoursOf = (i) => (status(i).key === "none" ? "–" : `${T.h(totalD(i))} / ${T.h(GOAL)} h`);

  function dayStrip(cur) {
    const sel = selDay(), live_ = live();
    const g = gate(sel), ready = readyDays();
    const cards = DAYNAMES.map((n, i) => {
      const st = status(i), date = T.addDays(T.monday(), i), isToday = live_ && i === TODAY;
      return `<li><button type="button" class="ds-day t-${st.tone}" data-sub="pick" data-i="${i}" data-fk="day${i}" aria-pressed="${i === sel}" aria-label="${DAYNAMES[i]} ${date.getDate()} : ${st.label}, ${hoursOf(i)}${isToday ? ", aujourd'hui" : ""}">
        <span class="ds-name">${cap(n)} ${date.getDate()}${isToday ? '<em>aujourd\'hui</em>' : ""}</span>
        <span class="ds-chip">${st.icon ? T.icon(st.icon, "sm") : '<span class="dot"></span>'}<span>${st.label}</span></span>
        <span class="ds-h">${hoursOf(i)}</span></button></li>`;
    }).join("");
    const onPage = cur.id === "soumettre";
    return `<div class="daystrip" role="group" aria-label="État d'envoi par jour">
      <ul class="ds-list">${cards}</ul>
      ${live_ ? `<div class="ds-actions">
        <button type="button" class="btn primary" data-sub="send-day" data-fk="strip-send" ${g.ok || g.aria ? "" : "disabled"} ${g.aria ? 'aria-disabled="true"' : ""} title="${esc(g.reason)}">${T.icon("soumettre", "sm")}<span>${g.ok ? `Soumettre ce jour · ${dayLabel(sel)}` : esc(g.label)}</span></button>
        <button type="button" class="btn" data-sub="send-ready" data-fk="strip-ready" ${ready.length && !M.busy ? "" : "disabled"} title="Envoie, un par un, les jours prêts (aucun conflit, aucune suppression).">${T.icon("check", "sm")}<span>Soumettre les jours prêts (${ready.length})</span></button>
        ${onPage ? "" : `<a class="btn link" href="#/soumettre">Voir le détail</a>`}
      </div>` : ""}</div>`;
  }

  function stepSub() {
    if (!live()) return { cls: "", sub: "" };
    const n = todoCount();
    if (M.busy) return { cls: "attn", sub: "envoi…" };
    if (n) return { cls: "attn", sub: `${n} jour${n > 1 ? "s" : ""} à traiter` };
    return { cls: "done", sub: "tout envoyé" };
  }

  /* ---- Rendu : corps de la page ---- */
  const badgeFor = { new: ["ok", "Nouveau — sera écrit"], same: ["info", "Déjà dans Forecast — ignoré (pas de doublon)"], changed: ["warn", "Modifié depuis l'envoi"], deleted: ["danger", "Supprimé localement"], conflict: ["danger", "Conflit — Forecast modifié ailleurs"], ignored: ["", "Ignoré (Ne pas envoyer)"], locked: ["accent", "Verrouillé — non écrit"], notattempted: ["warn", "Non tenté (envoi interrompu)"] };

  function resultCell(i, r) {
    const { t, c, st, op } = r, key = i + ":" + t.id, cur = M.res[key] || "keep";
    let [tone, label] = badgeFor[st];
    if (st === "same" && c.w) { tone = "ok"; label = "Écrit et vérifié ✓"; }
    let note = "";
    if (st === "new") note = `+${T.hh(c.D)} ajoutés dans Forecast`;
    if (st === "same") note = c.w ? "écrit pendant cet envoi" : "heures identiques : aucune écriture";
    if (st === "changed") note = M.opts.update ? `Forecast ${T.h(c.F)} → ${T.h(c.D)} h : <strong>mise à jour</strong> de la cellule existante ${tag("new")}` : `Forecast garde ${T.h(c.F)} h (mise à jour désactivée)`;
    if (st === "deleted") note = `Forecast garde ${T.h(c.F)} h : l'application ne supprime jamais seule`;
    if (st === "conflict") note = `Forecast ${T.h(c.F)} h ≠ brouillon ${T.h(c.D)} h : cellule non modifiée`;
    if (st === "ignored") note = "exclue de l'envoi pour la semaine";
    if (st === "locked") note = "période approuvée / facturée dans Forecast";
    if (st === "notattempted") note = "sera écrite au rejeu";
    let ctl = "";
    if (st === "conflict" || st === "deleted") {
      const opts = [["keep", "Ne rien modifier (comportement actuel)"], ["accept", `Aligner le brouillon sur Forecast (${T.h(c.F)} h) — à construire`]];
      if (st === "conflict" && c.D > (c.F || 0)) opts.push(["complement", `Ajouter le complément (+${T.h(c.D - c.F)} h) — à construire`]);
      if (st === "deleted") opts.push(["remove", "Supprimer dans Forecast — à construire (confirmation)"]);
      ctl = `<label class="res"><span class="visually-hidden">Résolution pour ${esc(t.name)}</span><select data-sub-change="res" data-key="${key}" data-fk="res-${t.id}">${opts.map((o) => `<option value="${o[0]}" ${o[0] === cur ? "selected" : ""}>${o[1]}</option>`).join("")}</select></label>`;
    }
    return `<span class="badge ${tone}">${esc(label)}</span><span class="rnote muted">${note}</span>${ctl}`;
  }

  function dayPanel(i) {
    const d = M.days[i], st = status(i), p = plan(i), g = gate(i);
    const date = T.addDays(T.monday(), i), title = cap(T.fmt(date, { weekday: "long", day: "numeric", month: "long" }));
    if (st.key === "none") return `<div class="card"><div class="card-head"><h2 class="sub-h">${esc(title)}</h2><span class="badge">Non saisi</span></div><p class="muted" style="margin:0">Aucune donnée pour ce jour (jour à venir). Rien à soumettre.</p></div>`;
    const dFp = fp(i, "D"), sFp = d.sentFp || "—";
    const same = d.sentFp && dFp === d.sentFp;
    const rows = p.rows.map((r) => {
      const { t, c } = r, ign = !!M.ignored[t.id];
      return `<tr class="st-${r.st}"><th scope="row"><span class="task-chip" style="background:${t.color}"></span>${esc(t.project)} <span class="muted">/ ${esc(t.name)}</span></th>
        <td class="num">${T.h(c.D)}</td><td class="num">${c.F == null ? "–" : T.h(c.F)}</td><td class="num">${c.S == null ? "–" : T.h(c.S)}</td>
        <td class="res-cell">${resultCell(i, r)}</td>
        <td class="num"><label class="ign"><input type="checkbox" data-sub-change="ignore" data-task="${t.id}" data-fk="ign-${t.id}" ${ign ? "checked" : ""}><span class="visually-hidden">Ne pas envoyer ${esc(t.name)}</span><span aria-hidden="true">Ne pas envoyer</span></label></td></tr>`;
    }).join("");
    const np = d.noproject ? `<tr class="st-noproject"><th scope="row"><span class="task-chip" style="background:var(--muted)"></span>Temps sans projet</th><td class="num">${T.h(d.noproject)}</td><td class="num">–</td><td class="num">–</td><td class="res-cell"><span class="badge">Non envoyé — sans projet</span><span class="rnote muted">non saisissable dans Forecast, hors objectif</span></td><td></td></tr>` : "";
    const c = p.counts;
    const summary = `<ul class="sum" aria-label="Résumé de la simulation"><li><strong>${c.write}</strong> à écrire</li><li><strong>${c.already}</strong> déjà dans Forecast</li><li><strong>${c.ignored}</strong> ignorée${c.ignored > 1 ? "s" : ""}</li><li><strong>${c.notSent}</strong> non envoyée${c.notSent > 1 ? "s" : ""}</li></ul>`;
    const notice = M.notice ? `<div class="alert ${M.notice.tone === "info" ? "" : M.notice.tone}" role="${M.notice.role || "status"}"><span class="body">${esc(M.notice.text)}</span></div>` : "";
    const alerts = [];
    if (d.partial) alerts.push(`<div class="alert danger"><span class="body"><strong>Envoi partiel :</strong> ${TASKS.filter((t) => d.cells[t.id].w).length} cellule(s) écrite(s) et vérifiée(s), ${p.rows.filter((r) => r.st === "notattempted").length} non tentée(s). La semaine n'est <em>pas</em> marquée « envoyée ». ${M.stale ? "Relisez Forecast pour rafraîchir la simulation, puis rejouez." : "Simulation à jour : vous pouvez rejouer."}</span></div>`);
    if (st.key === "modified") alerts.push(`<div class="alert warn"><span class="body"><strong>Modifié depuis l'envoi du ${esc(d.sentAt)} :</strong> l'empreinte du brouillon (<code>${dFp}</code>) ne correspond plus à celle envoyée (<code>${sFp}</code>). Un jour envoyé n'est pas renvoyé tel quel : seules les différences sont proposées.</span></div>`);
    if (p.unresolved.length) alerts.push(`<div class="alert danger"><span class="body"><strong>Conflit :</strong> Forecast a été modifié ailleurs. L'application ne l'écrase pas ; choisissez une résolution dans le tableau.</span></div>`);
    if (st.key === "sent") alerts.push(`<div class="alert ok"><span class="body"><strong>Jour envoyé et à jour.</strong> Même empreinte (<code>${dFp}</code>) : un nouveau clic n'écrirait rien.</span></div>`);
    const busyTxt = M.busy ? (M.busy.reading ? "Lecture de Forecast…" : `Écriture ${M.busy.done}/${M.busy.total}…`) : "";
    const sendBtn = `<button type="button" class="btn primary" data-sub="send-day" data-fk="send" ${g.ok || g.aria ? "" : "disabled"} ${g.aria ? 'aria-disabled="true"' : ""}>${M.busy ? '<span class="spin" aria-hidden="true"></span>' : T.icon("soumettre", "sm")}<span>${esc(g.label)}</span></button>`;
    return `<div class="card sub-day">
      <div class="card-head"><h2 class="sub-h">${esc(title)} ${i === TODAY ? '<span class="muted" style="font-weight:400">(aujourd\'hui)</span>' : ""}</h2><span class="badge ${st.tone === "sent" ? "ok" : st.tone === "gap" ? "danger" : st.tone === "modified" ? "warn" : st.tone === "ready" ? "accent" : "info"}">${st.icon ? T.icon(st.icon, "sm") : ""}${esc(st.label)}</span></div>
      <p class="muted sub-meta">Dernier envoi : <strong>${d.sentAt ? esc(d.sentAt) : d.partial ? "interrompu" : "jamais"}</strong> · empreinte brouillon <code>${dFp}</code> / envoyée <code>${sFp}</code> ${d.sentFp ? (same ? '<span class="badge ok">identique</span>' : '<span class="badge warn">différente</span>') : ""} ${tag("new")}</p>
      ${alerts.join("")}${notice}
      ${summary}
      <div class="table-wrap"><table class="sim"><thead><tr><th scope="col">Tâche</th><th scope="col" class="num">Brouillon (h)</th><th scope="col" class="num">Forecast (h)</th><th scope="col" class="num">Envoyé (h)</th><th scope="col">Résultat de la simulation</th><th scope="col" class="num">Semaine ${tag("exist")}</th></tr></thead><tbody>${rows}${np}
        <tr class="tot"><th scope="row">Total du jour</th><td class="num"><strong>${T.h(totalD(i))}</strong></td><td colspan="4" class="muted">Objectif ${T.hh(GOAL)}${eq(totalD(i), GOAL) ? " · atteint" : " · non atteint"}</td></tr></tbody></table></div>
      <div class="sub-actions" role="group" aria-label="Actions d'envoi">
        ${sendBtn}
        <button type="button" class="btn" data-sub="reread" data-fk="reread" ${M.busy && !g.aria ? "disabled" : ""}>${T.icon("refresh", "sm")}<span>Relire Forecast (simulation)</span></button>
        ${M.busy && !M.busy.reading ? `<button type="button" class="btn danger-outline" data-sub="cancel" data-fk="cancel">Annuler ${tag("new")}</button>` : ""}
        <span class="sub-busy" role="status">${busyTxt}</span>
      </div>
      <p class="muted sub-reason">${esc(g.reason)}</p>
      <label class="opt"><input type="checkbox" data-sub-change="upd" data-fk="upd" ${M.opts.update ? "checked" : ""}> Mettre à jour dans Forecast les heures modifiées depuis l'envoi ${tag("new")}</label>
    </div>`;
  }

  function history(i) {
    const rows = [];
    (UI.histAll ? [0, 1, 2, 3, 4] : [i]).forEach((k) => M.days[k].log.forEach((l, n) => rows.push({ k, l, n })));
    rows.sort((a, b) => 0);
    const tone = { ok: "ok", danger: "danger", warn: "warn", info: "info" };
    return `<details class="card" data-ui="hist" ${UI.hist ? "open" : ""}><summary class="card-head"><h3>Journal des envois ${tag("new")}</h3><span class="badge">${rows.length}</span></summary>
      <label class="opt"><input type="checkbox" data-sub-change="histall" data-fk="histall" ${UI.histAll ? "checked" : ""}> Toute la semaine</label>
      ${rows.length ? `<div class="table-wrap"><table><thead><tr><th>Horodatage</th>${UI.histAll ? "<th>Jour</th>" : ""}<th>Action</th><th>Résultat</th><th>Empreinte</th></tr></thead><tbody>${rows.map(({ k, l }) => `<tr><td class="muted">${esc(l.t)}</td>${UI.histAll ? `<td>${dayLabel(k)}</td>` : ""}<td>${esc(l.a)}</td><td><span class="badge ${tone[l.r] || ""}">${esc(l.x)}</span></td><td><code>${l.fp}</code></td></tr>`).join("")}</tbody></table></div>` : '<p class="muted" style="margin:8px 0 0">Aucun envoi pour ce jour.</p>'}</details>`;
  }

  function guards(i) {
    const d = M.days[i];
    const item = (t, title, txt) => `<li>${tag(t)}<div><strong>${title}</strong><span class="muted">${txt}</span></div></li>`;
    return `<details class="card" data-ui="guards" ${UI.guards ? "open" : ""}><summary class="card-head"><h3>Garde-fous anti-doublon</h3><span class="badge info">8 mécanismes</span></summary>
      <ul class="guards">
        ${item("exist", "Verrou d'opération Forecast", `${M.busy ? "<strong>occupé</strong> — un 2ᵉ clic est refusé" : "libre"} (une seule opération à la fois ; message « Une opération Forecast est déjà en cours »).`)}
        ${item("partial", "Simulation fraîche exigée", `${M.stale ? "<strong>périmée</strong> — relire Forecast" : `relue à ${M.readAt}`} · empreinte du brouillon vérifiée juste avant d'écrire (aujourd'hui sur la semaine entière).`)}
        ${item("exist", "Relecture de Forecast avant chaque écriture", "Une cellule déjà présente (mêmes heures) est ignorée : jamais de doublon, même après une interruption.")}
        ${item("exist", "Rejeu après interruption", "N'écrit que les cellules manquantes ; « Ne pas envoyer » par tâche et par semaine.")}
        ${item("new", "Statut, horodatage et empreinte par jour", `Dernier envoi : ${d.sentAt ? esc(d.sentAt) : "—"} · <code>${d.sentFp || "—"}</code> (aujourd'hui : un seul marqueur pour la semaine).`)}
        ${item("new", "Bouton désactivé « Rien de nouveau à envoyer »", "Calculé à partir de la simulation (aucune cellule à écrire).")}
        ${item("new", "Confirmation seulement si risqué", "Conflit résolu par complément ou suppression dans Forecast. Une écriture simple ne demande rien.")}
        ${item("new", "Annuler et journal par jour", "Annuler s'arrête avant la cellule suivante ; aucune annulation côté Forecast (à construire).")}
      </ul></details>`;
  }

  function scenarioCard() {
    return `<details class="card scen" data-ui="scen" ${UI.scen ? "open" : ""}><summary class="card-head"><h3>Scénarios de démonstration</h3><span class="badge accent">${esc(SCENARIOS.find((s) => s.id === M.scenario).label)}</span></summary>
      <div class="scen-grid" role="radiogroup" aria-label="Scénario">${SCENARIOS.map((s) => `<label class="scen-opt ${s.id === M.scenario ? "on" : ""}"><input type="radio" name="scenario" value="${s.id}" data-sub-change="scenario" data-fk="sc-${s.id}" ${s.id === M.scenario ? "checked" : ""}><span><strong>${esc(s.label)}</strong><small>${esc(s.desc)}</small></span></label>`).join("")}</div>
      <div class="sub-actions"><button type="button" class="btn" data-sub="dbl" data-fk="dbl" ${M.busy ? "disabled" : ""}>Simuler un double clic</button>
        <label class="opt"><input type="checkbox" data-sub-change="inject" data-fk="inject" ${M.inject ? "checked" : ""}> Provoquer une panne après 2 cellules (prochain envoi)</label>
        <button type="button" class="btn link" data-sub="reset" data-fk="reset">Réinitialiser le scénario</button></div></details>`;
  }

  function legend() {
    return `<details class="card" data-ui="legend" ${UI.legend ? "open" : ""}><summary class="card-head"><h3>Légende : réel ou à construire ?</h3><span class="badge">lire</span></summary>
      <p style="margin:0 0 8px">${tag("exist")} fonctionne déjà dans l'application · ${tag("partial")} existe, mais sur la semaine et pas par jour · ${tag("new")} nouveau : à construire.</p>
      <p class="muted" style="margin:0 0 8px"><strong>Statuts d'un jour :</strong> <em>Non saisi</em> (aucune heure) · <em>Brouillon</em> (journée en cours, sous l'objectif) · <em>Prêt</em> (objectif atteint, cellules à écrire) · <em>Envoyé ✓</em> (tout est dans Forecast) · <em>Modifié depuis l'envoi ⚠</em> (le brouillon diverge de ce qui a été envoyé) · <em>Écart</em> (heures hors objectif ou conflit Forecast : l'envoi est bloqué).</p>
      <p class="muted" style="margin:0">Données de démonstration : Jeu. 22 et Ven. 23 sont à venir. Aujourd'hui = mer. 21 octobre 2026.</p></details>`;
  }

  function body() {
    if (!live()) return `<div class="alert"><span class="body">Cette maquette ne contient de données de soumission que pour la semaine courante. Revenez à « cette semaine » (touche <kbd>[</kbd> ou <kbd>]</kbd>).</span></div>`;
    const i = selDay();
    return `${scenarioCard()}${dayPanel(i)}${history(i)}${guards(i)}${legend()}`;
  }

  /* ---- Événements ---- */
  document.addEventListener("click", (e) => {
    const b = e.target.closest("[data-sub]");
    if (!b) return;
    e.preventDefault();
    if (b.disabled) return;
    const a = b.dataset.sub;
    if (a === "pick") { M.sel = +b.dataset.i; M.notice = null; refresh(); }
    else if (a === "send-day") send([selDay()]);
    else if (a === "send-ready") send(readyDays());
    else if (a === "cancel") { M.cancel = true; T.toast("Annulation après la cellule en cours…"); }
    else if (a === "reread") reread();
    else if (a === "dbl") { const i = selDay(); send([i]); setTimeout(() => send([i]), 140); }
    else if (a === "reset") { load(M.scenario); refresh(); }
  });
  document.addEventListener("change", (e) => {
    const el = e.target.closest("[data-sub-change]");
    if (!el) return;
    const k = el.dataset.subChange;
    if (k === "scenario") { load(el.value); M.sel = null; }
    else if (k === "ignore") { if (el.checked) M.ignored[el.dataset.task] = true; else delete M.ignored[el.dataset.task]; }
    else if (k === "upd") M.opts.update = el.checked;
    else if (k === "res") M.res[el.dataset.key] = el.value;
    else if (k === "inject") M.inject = el.checked;
    else if (k === "histall") UI.histAll = el.checked;
    M.notice = null;
    refresh();
  });
  document.addEventListener("toggle", (e) => { const k = e.target.dataset && e.target.dataset.ui; if (k) UI[k] = e.target.open; }, true);

  /* ---- Branchement sur la page « Soumettre » ---- */
  load(new URLSearchParams(location.search).get("scenario") || "neuf");
  const P = T.byId("soumettre");
  P.desc = "Envoyer jour par jour, sans doublon";
  P.actions = [];
  P.body = body;
  Object.defineProperty(P, "badge", { configurable: true, get() { const n = todoCount(); return n ? { text: String(n), tone: "warn", label: `${n} jour${n > 1 ? "s" : ""} à traiter` } : null; } });

  window.TSHS = { dayStrip, status, stepSub, todoCount, load: (id) => { load(id); M.sel = null; T.emit(); } };
})();
