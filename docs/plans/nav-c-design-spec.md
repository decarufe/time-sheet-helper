# Spécification de design — Navigation « C » (rail) + soumission « C+ »

> Lot WP2 · Martha Stewart (Design) · branche `squad/nav-c` · statut : **prêt pour revue** (aucun code de production).
> Sources lues : `docs\nav-prototypes\shared\base.css`, `c-rail\index.html`, `c-rail\submit.css`, `c-rail\submit.js`, `src\TimeSheetHelper.App\wwwroot\app.css`, `Components\Layout\MainLayout.razor(.css)`, CSS scopés des pages.
> Décisions appliquées : D4 (objectif quotidien = `Engine:TargetHoursPerDay`, 8 h, **référence non bloquante** ; l'objectif qui compte est le 40 h hebdomadaire), D5 (pas de Ctrl+K), D6 (barre mobile portée ; cartes de démonstration exclues ; garde-fous = texte d'aide).
> **Alignement sur `nav-c-contracts.md` (WP1)** : les **noms** (composants, enums `CellState`/`DayStatus`/`DayGate`, libellés, clés `localStorage`, raccourcis) suivent les contrats ; le **rendu visuel** (icônes, jetons, contrastes) suit cette spec. Les écarts visuels délibérés sont listés au §7.1 (tableau « Correspondance ») et suivis par CR-6.
> Tous les ratios ci-dessous sont des **ratios WCAG 2.x calculés** (luminance relative sRGB) ; les teintes translucides sont composées sur le fond réel (`--bg` ou `--surface`) avant calcul.

---

## 1. Principes

1. **Jamais la couleur seule** : tout état = couleur + icône (forme distincte) + libellé texte (§7).
2. **AA partout** : texte ≥ 4,5:1 ; texte ≥ 18 px gras / composants d'interface et indicateurs (focus, bordure de contrôle, icône porteuse de sens) ≥ 3:1.
3. **Deux thèmes complets** : clair et sombre, mêmes noms de jetons ; la valeur change, jamais le nom.
4. **Un seul jeu de jetons** dans `app.css` (§2.4) ; les CSS scopés des pages ne redéfinissent plus les jetons partagés.
5. **Clavier d'abord** : chaque action est atteignable et visible au focus ; les raccourcis sont des accélérateurs, jamais la seule voie.
6. **Mouvement minimal** et entièrement neutralisé par `prefers-reduced-motion` (§9).

---

## 2. Jetons de design

### 2.1 Mécanisme de thème

- Attribut `data-theme="light|dark"` sur `<html>` ; absence d'attribut = thème système.
- Sélecteurs (à reproduire **exactement** — les deux sont requis) :
  ```css
  :root { /* valeurs claires */ }
  :root[data-theme="dark"] { /* valeurs sombres */ }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) { /* mêmes valeurs sombres */ }
  }
  ```
- Persistance : `localStorage["tsh.theme"]` ∈ {`light`,`dark`}, absent = système (clé confirmée par `nav-c-contracts.md` ; script anti-flash en ligne dans `App.razor`, module `shell.js`). Basculer = Alt+T (le bouton du rail porte aussi l'action). `color-scheme: light dark` déclaré sur `:root` (barres de défilement et contrôles natifs).
- Éviter le flash : `shell.js` applique `data-theme` **avant** le premier rendu (script en tête de `App.razor`).

### 2.2 Jetons de base (reprise du prototype, identiques aux valeurs actuelles de `app.css` pour les couleurs existantes)

| Jeton | Clair | Sombre | Ratio texte/`--bg` (C / S) | Ratio sur `--surface` (C / S) | Usage |
| --- | --- | --- | --- | --- | --- |
| `--bg` | `#ffffff` | `#0d1117` | — | — | fond de page |
| `--fg` | `#1f2328` | `#e6edf3` | **15,80 / 16,02** | 14,84 / 14,65 | texte principal |
| `--muted` | `#656d76` | `#8d96a0` | **5,25 / 6,31** | 4,93 / 5,77 | texte secondaire (jamais sur teinte, voir §2.5) |
| `--border` | `#d0d7de` | `#30363d` | 1,45 / 1,55 (décoratif) | — | séparateurs, contours de carte (non porteurs de sens) |
| `--border-strong` **(nouveau)** | `#6e7781` | `#7d8590` | **4,55 / 5,07** | 4,27 / 4,64 | contour des **contrôles** (champ, case, bouton secondaire) ≥ 3:1 |
| `--accent` | `#0969da` | `#4493f8` | **5,19 / 6,11** | 4,88 / 5,59 | liens, focus, état actif, bouton principal (fond) |
| `--surface` **(nouveau, = `--row-alt`)** | `#f6f8fa` | `#151b23` | — | — | cartes, panneau, rail, bande de jours |
| `--row-alt` | `#f6f8fa` | `#161b22` | — | — | alternance de lignes (existant, conservé) |
| `--on-accent` **(nouveau)** | `#ffffff` | `#0d1117` | **5,19 / 6,11** sur `--accent` | — | texte/icône posé sur fond `--accent` |
| `--shadow` **(nouveau)** | `0 8px 24px rgba(31,35,40,.16)` | `0 8px 24px rgba(1,4,9,.6)` | — | — | tiroir, info-bulle, popover |
| `--radius` **(nouveau)** | `8px` | `8px` | — | — | cartes, panneaux ; pastilles = `999px`, bouton = `6px`, lien rail = `10px` |

> ⚠ **Correction du prototype** : le prototype met du texte `#fff` sur `--accent` sombre (`#4493f8`) = **3,10:1 ✘**. On introduit `--on-accent` (`#0d1117` en sombre = 6,11 ✔). Même règle pour tout fond plein de statut : **jamais de blanc sur un statut plein en sombre** (blanc/`#3fb950` = 2,54 ✘ ; blanc/`#f85149` = 3,35 ✘). Les statuts sont rendus en **teinte + texte coloré** (§2.3), pas en aplat plein.

### 2.3 Jetons de statut (teinte = couleur à opacité fixe, composée sur le fond)

Deux familles par statut : `--X` (couleur pleine : icône, filet, point, bordure ≥ 3:1) et `--X-text` **(nouveau)** (couleur de **texte** garantie ≥ 4,5:1 sur la teinte, y compris sur `--surface`), plus `--X-bg` (teinte).

| Statut | `--X` clair | `--X` sombre | `--X-bg` clair | `--X-bg` sombre | `--X-text` clair | `--X-text` sombre |
| --- | --- | --- | --- | --- | --- | --- |
| `ok` | `#1a7f37` | `#3fb950` | `rgba(26,127,55,.10)` | `rgba(63,185,80,.15)` | `#116329` | `#3fb950` |
| `warn` | `#9a6700` | `#d29922` | `rgba(154,103,0,.12)` | `rgba(210,153,34,.16)` | `#7d4e00` | `#d29922` |
| `danger` | `#cf222e` | `#f85149` | `rgba(207,34,46,.10)` | `rgba(248,81,73,.15)` | `#a40e26` | `#ff7b72` |
| `info` | `#57606a` | `#9198a1` | `rgba(87,96,106,.12)` | `rgba(145,152,161,.16)` | `#57606a` | `#9198a1` |
| `accent` (`--accent-bg`) | `#0969da` | `#4493f8` | `rgba(9,105,218,.10)` | `rgba(68,147,248,.16)` | `#0550ae` | `#58a6ff` |
| `locked` | `#8250df` | `#a371f7` | `rgba(130,80,223,.12)` | `rgba(163,113,247,.16)` | `#6639ba` | `#b392f7` |

Alias conservés : `--idle` = `--warn`. `--info-bg` du prototype/app = teinte **grise** (`--info`) ; voir collision §2.4.

**Ratios calculés** (texte `--X-text` sur sa propre teinte, composée sur `--bg` / sur `--surface`) — tous ≥ 4,5 ✔ :

| Statut | Clair : couleur pleine `--X` / `--bg` | Clair : `--X-text` sur teinte/`--bg` | Clair : sur teinte/`--surface` | Sombre : `--X` / `--bg` | Sombre : `--X-text` sur teinte/`--bg` | Sombre : sur teinte/`--surface` |
| --- | --- | --- | --- | --- | --- | --- |
| `ok` | 5,08 | **6,45** | **6,09** | 7,45 | **5,98** | **5,32** |
| `warn` | 4,87 | **6,08** | **5,74** | 7,50 | **5,88** | **5,29** |
| `danger` | 5,36 | **6,71** | **6,33** | 5,65 (`#f85149`) | **6,40** | **5,80** |
| `info` | 6,39 | **5,40** | **5,10** | 6,50 | **5,16** | **4,63** |
| `accent` | 5,19 | **6,59** | **6,23** | 6,11 | **6,05** | **5,44** |
| `locked` | 5,05 | **6,25** | **5,90** | 5,64 | **6,21** | **5,54** |

Ce que le calcul a **invalidé** dans le prototype (texte coloré `--X` directement sur teinte posée sur `--surface`) : clair `ok` 4,19 ✘, `warn` 3,94 ✘, `danger` 4,31 ✘, `accent` 4,26 ✘, `locked` 4,05 ✘ ; sombre `danger` 4,37 ✘, `accent` 4,43 ✘, `locked` 4,15 ✘. → **Règle** : tout texte de badge/alerte utilise `--X-text`, jamais `--X`. `--X` reste réservé aux icônes, points, filets et bordures (≥ 3:1 vérifié contre `--surface` : minimum clair 4,57 (`warn`), sombre 5,16 (`danger`, `locked`)). Le jeton `--locked-bg` est nouveau (le prototype n'a pas de teinte `locked`).

Texte sur fond d'accent sélectionné (`--accent-bg`) : `--fg` 13,71 / 12,93 ✔ ; `--accent-text` 6,59 / 6,05 ✔ ; `--muted` **4,55 / 5,09 sur `--bg`**, mais **4,30 ✘ (clair) / 4,58 (sombre) sur `--surface`** → **le texte `--muted` ne se pose jamais sur une teinte posée sur `--surface`** (utiliser `--fg` ou `--accent-text`).

### 2.4 Migration, hissage et collisions (demandes de changement WP3/WP6)

- **Hisser dans `app.css`** (propriétaire WP3) : `--surface`, `--accent-bg`, `--on-accent`, `--border-strong`, `--shadow`, `--radius`, tous les `--X`, `--X-bg`, `--X-text` du §2.3, avec les **trois blocs** du §2.1 (la version actuelle d'`app.css` n'a que `@media (prefers-color-scheme: dark)` : le sélecteur `data-theme` manque → à ajouter).
- **CSS scopés à nettoyer** (WP6 + propriétaires de pages) : `Day`, `Week`, `ForecastTasks`, `Rules`, `Sources` redéfinissent `--ok/--warn/--danger/--accent-soft/--surface` en local, **uniquement** avec `@media (prefers-color-scheme: dark)` → **ces redéfinitions ne suivent pas `data-theme`** et casseraient le commutateur manuel (page claire dans un thème forcé sombre, ou l'inverse). Règle : supprimer les redéfinitions identiques aux jetons globaux ; pour les jetons réellement locaux (`--under`, `--over`, `--mid-bg`, `--low-bg`, `--low-fg`, `--error-bg/fg`, `--selected-bg`, `--stripe`, `--task-l`), les redéclarer avec le **double sélecteur** `:root[data-theme="dark"] .x` + `@media … :root:not([data-theme="light"]) .x`. Ratios locaux vérifiés : `--under` `#bc4c00` 5,03 / `#f0883e` 7,48 ; `--low-fg` `#953800` sur `--low-bg` 5,66 / `#ffa657` 6,65 ; `--over` `#8250df` 5,05 / `#a371f7` 5,64 ; `--error-fg` 4,57 / `#ff7b72` 6,49 — tous ✔.
- **Collision `--info-bg`** : `app.css` = gris (`rgba(101,109,118,.12)`), `Day`/`Week` = bleu (`rgba(9,105,218,.08)`). Renommer le bleu local en `--accent-soft`/`--accent-bg` ; `--info-bg` ne désigne que le gris. À faire en WP6 (propriétaire des CSS de pages).
- **Valeur existante à aligner** : `--info-bg` gris passe de `rgba(101,109,118,.12)` à `rgba(87,96,106,.12)` (même base que `--info`, ratios du §2.3 calculés avec cette base).

### 2.5 Règles d'usage (contrastes)

| Combinaison | Autorisée | Ratio minimum constaté |
| --- | --- | --- |
| `--fg` / `--bg`, `--surface`, `--accent-bg` | ✔ | 11,62 (sombre, accent-bg/surface) |
| `--muted` / `--bg`, `--surface` | ✔ | 4,93 (clair/surface) |
| `--muted` / toute teinte sur `--surface` | ✘ interdit | 4,30 |
| `--X-text` / `--X-bg` (sur `--bg` ou `--surface`) | ✔ | 4,63 (`info` sombre/surface) |
| `--X` (couleur pleine) comme **texte** | ✘ sauf `--accent`, `--ok`, `--danger` sur `--bg` seulement (≥ 5,08) | — |
| `--on-accent` / `--accent` (bouton principal) | ✔ | 5,19 |
| bordure de contrôle `--border-strong` / `--bg` | ✔ | 4,55 |
| `--accent` focus / `--bg`, `--surface` | ✔ (≥ 3:1) | 4,88 |
| info-bulle : `--bg` sur fond `--fg` (inversée) | ✔ | 15,80 / 16,02 |

---

## 3. Grille d'application

```
┌──────┬───────────┬──────────────────────────────────────────┐
│ rail │  panneau  │  en-tête (sticky)                        │
│ 60px │  248px    ├──────────────────────────────────────────┤
│      │ (0 si     │  main.page  (max 1120px, centré)         │
│      │ replié)   │                                          │
└──────┴───────────┴──────────────────────────────────────────┘
```

- `.shell` : `display:grid; grid-template-columns: var(--rail-w) var(--panel-w) 1fr; height:100vh;` avec `--rail-w:60px`, `--panel-w:248px`. Classe `.no-panel` → `--panel-w:0` (transition `grid-template-columns .18s ease`, neutralisée en mouvement réduit, §9). Le panneau replié reçoit `visibility:hidden` (hors ordre de tabulation) **après** la transition.
- Rail et panneau : `position:sticky; top:0; height:100vh; overflow-y:auto` ; fond `--surface` ; séparateur `1px solid var(--border)` côté droit.
- Colonne principale : `min-width:0; overflow-y:auto` (pas de défilement horizontal de page).
- En-tête `.head` : `position:sticky; top:0; z-index:40; background:var(--bg); border-bottom:1px solid var(--border)`. `.head-in` : `max-width:1120px; margin:0 auto; padding:14px 32px 12px`.
- Contenu `main.page` : `max-width:1120px; margin:0 auto; padding:22px 32px 56px`.
- Cible tactile/clic minimale : **44×44** pour les liens du rail et de la barre basse (le lien du rail fait 48×52 ✔), 32×32 minimum pour les boutons-icônes de bureau (≥ 24×24 exigé WCAG 2.2 AA 2.5.8).
- Défilement : le `<main>` est la région qui défile ; `scroll-padding-top` = hauteur d'en-tête (pour que le focus ne passe pas sous l'en-tête sticky — WCAG 2.4.11).

### 3.1 Échelle de superposition (z-index)

| Couche | z-index |
| --- | --- |
| contenu | auto |
| en-tête `.head` | 40 |
| rail / panneau | 60 |
| barre basse mobile | 70 |
| scrim du tiroir | 75 |
| tiroir mobile (panneau) | 80 |
| info-bulle `data-tip` | 200 |
| `ConfirmDialog` / modales existantes | 900 |
| `#blazor-error-ui` | 1000 |

### 3.2 Espacements et typographie

Base 4 px (4, 8, 12, 16, 24, 32). Police : pile système existante de l'app. Corps 16 px (`1rem`), secondaire `.88rem`, badge `.78rem` (≥ 12,5 px ; le ratio du badge est calculé ≥ 4,5 donc toléré ; **ne pas descendre sous `.78rem`**), titre de page 1,35–1,5 rem. `html { font-size:100% }` : tout en `rem`, respecter le zoom 200 %.

---

## 4. Point de rupture 860 px (responsive)

`@media (max-width: 860px)` — **un seul** point de rupture structurel.

| Élément | > 860 px | ≤ 860 px |
| --- | --- | --- |
| Rail | visible (60 px) | **masqué** (`display:none`) |
| Panneau contextuel | colonne 248 px, repliable | **tiroir** hors-canevas (voir `MobileNav`/tiroir §5.5) |
| Barre basse (4 sections) | masquée | **fixe en bas** (`position:fixed; inset:auto 0 0; z-index:70`), `padding-bottom:env(safe-area-inset-bottom)` |
| Sous-navigation (`.subnav`) | masquée | **pastilles** défilantes sous l'en-tête : pages de la section courante |
| Bouton « panneau » (`.toggle-panel`) | visible | masqué ; remplacé par le bouton « Menu » du tiroir |
| Outils `.m-tools` (surveillance + thème) | dans `.r-foot` du rail | visibles dans l'en-tête |
| Stepper | libellés + sous-titres | **libellé de l'étape courante seulement** ; numéros/icônes `done` visibles pour toutes ; la **barre de progression passe en pleine largeur** |
| Bande de jours (DayStrip) | grille 5 colonnes | **défilement horizontal**, cartes 132 px, `scroll-snap-type:x mandatory` |
| `main.page` | padding `22 32 56` | padding `16 16 96` (`96 px` bas = hauteur barre + marge, rien n'est masqué) |
| Tableaux larges | — | conteneur `.table-wrap` à défilement horizontal, en-têtes conservés |

Valeur de largeur de référence mobile : **390×844** ; largeur minimale prise en charge : 320 px (aucun débordement horizontal de page, vérifié en WP7).

---

## 5. Spécification des composants (Phase 1)

Convention pour chaque fiche : **Structure / ARIA · États · Jetons · Clavier**.

### 5.1 `Rail` (`Layout\Rail.razor`)

- **Structure** : `<nav class="rail" aria-label="Sections">` ; logo (`role="img" aria-label="Time Sheet Helper"`, 32 px) ; `.rail-links` = **4 sections** (Temps → icône `semaine`, Envoi → `soumettre`, Config → `regles`, Avancé → `activite`) ; `.rail-gap` ; `.r-foot` = bouton Surveillance (`track`, `.on`/`.paused`) et bouton Thème. Chaque lien = icône 20 px + libellé court 11 px (visible, `aria-hidden` car le nom accessible est l'`aria-label` long : « Ma semaine », « Soumettre », « Configuration », « Avancé »).
- Chaque section pointe vers **la dernière page visitée** de la section (sinon la page d'accueil de la section : `/`, `/soumettre`, `/regles`, `/activite`). les 7 destinations restent atteignables par le panneau et par Alt+1..7 (voir §5.2 / prototype). Noms de sections (`NavSection`) : `Time` « Ma semaine », `Sending` « Soumettre », `Configuration`, `Advanced` « Avancé » ; libellés de destinations : Semaine, Jour, Soumettre, Règles, Tâches, Sources, Activité brute.
- **ARIA** : section courante `aria-current="true"` ; badge numérique (ex. « 2 » jours à traiter) = `<span class="badge warn" title="…">2<span class="visually-hidden"> — 2 jours à traiter</span></span>` (le sens ne repose pas sur la couleur). Boutons à bascule : `aria-pressed` (surveillance) avec `aria-label` dynamique (« Surveillance active, cliquer pour mettre en pause »). Thème : `aria-label="Basculer le thème clair/sombre"`.
- **États** : repos = icône `--muted` sur transparent (muted/surface 4,93 / 5,77 ✔) ; survol = fond `--accent-bg`, icône `--fg` ; **actif** = fond `--accent-bg` + **filet gauche 3 px `--accent`** + icône et libellé `--accent-text` (6,23 / 5,44 ✔) — l'état actif n'est donc pas porté par la seule couleur (forme : filet + `aria-current`) ; focus = §8. Surveillance `.on` = point `--ok` + icône `play`/`pause` (forme) ; `.paused` = icône `pause` + point `--warn`.
- **Dimensions** : lien 48×52, rayon 10 px, gap 6 px ; rail 60 px de large.
- **Info-bulle** (`data-tip`) : fond `--fg`, texte `--bg`, rayon 6 px, `.82rem`, à droite (`left:calc(100% + 10px)`), z 200 ; affichée **au survol ET au focus clavier** (`:hover` + `:focus-visible`) ; **non requise pour le nom accessible** (déjà dans `aria-label`) ; ne bloque pas le pointeur (`pointer-events:none`) ; Échap la ferme si on ajoute un déclencheur au clic (non requis).
- **Clavier** : `Tab` parcourt les liens dans l'ordre visuel ; `Entrée` active. Alt+1..7 = pages (ordre : Semaine, Jour, Soumettre, Règles, Tâches, Sources, Activité) ; Alt+T = thème ; Ctrl+B = panneau ; `[` `]` = semaine précédente/suivante. Règles de garde (contrats §2, `ShortcutMap.cs`) : **Alt+1..7 et Alt+T restent actifs même dans un champ** (ignorés si Ctrl/Méta est enfoncé) ; **`[` `]` sont ignorés** dans un champ éditable (`input`, `textarea`, `select`, `[contenteditable]`) ou si Ctrl/Méta/Alt est enfoncé ; **Ctrl/Méta+B est ignoré** dans un champ éditable. Pas de Ctrl+K (D5). Les raccourcis sont exposés dans l'aide (§5.2).

### 5.2 `ContextPanel` (`Layout\ContextPanel.razor`)

- **Structure** : `<aside class="panel" id="panel" aria-label="Panneau de section"><div class="p-in">…</div></aside>`. Contenu selon la section :
  - **Temps** : titre « Ma semaine » + « S{n} » ; liens de pages (`.p-link`, `aria-current="page"`) ; sous-titre « Jours de la semaine » ; 5 `.day-link` (point d'état + libellé + heures).
  - **Envoi** : titre « Avant d'envoyer » ; liens de pages ; « Liste de vérification » (`.check.ok` / `.check.todo` : icône `check` ou `warn` + texte + sous-texte) ; ligne « N jours à traiter » (C+).
  - **Config** : liens Règles/Tâches/Sources. **Avancé** : lien Activité brute + texte d'aide.
- **`.day-link`** : `[point état][Lun 19 (aujourd'hui)][heures]`. Le point seul ne porte pas le sens : **point + libellé d'état en `title` ET en texte visuellement masqué** (« Lun 19, Complet, 8 h »). Formes de point (distinctes, pas seulement la couleur) : `ok` = disque plein avec ✓ ; `warn` = triangle/« ! » ; `none` = anneau vide. Les points se dessinent en SVG 12 px (icônes `check`, `warn`, anneau) et non en simple `border-radius`.
- **États** : repos = `--fg` ; `.p-link:hover` fond `--accent-bg` ; courant = fond `--accent-bg`, texte `--fg` gras, `aria-current="page"` (pas seulement la couleur : poids + filet gauche 3 px `--accent`) ; `.p-link .d` (description) en `--muted` sur `--surface` (4,93 / 5,77 ✔ ; **jamais** sur l'état courant teinté : y passer en `--fg`). `.today-tag` = badge `info` « aujourd'hui ».
- **Objectif** (D4) : afficher « Xh / 8h » en référence de la journée, **sans** état d'erreur bloquant ; l'état d'avancement fort est le total **hebdomadaire** (ex. « 36 h / 40 h »).
- **Repli** : bouton `.toggle-panel` (`aria-expanded`, `aria-controls="panel"`, titre « Panneau (Ctrl+B) ») ; préférence `localStorage["tsh.panel"]` = `open|closed` ; Ctrl+B bascule ; panneau fermé = `visibility:hidden` + `inert`.
- **Clavier** : liens dans l'ordre du DOM ; Ctrl+B ; en tiroir mobile, voir §5.5.

### 5.3 `WeekHeader` (`Layout\WeekHeader.razor`)

- **Structure** : `<header class="head" id="head">` > `.head-in` > `.head-row` : `kicker` (« Semaine 29 · 13–19 juil. »), `<h1>` (titre de la page, `tabindex="-1"`, reçoit le focus programmatique à chaque navigation, `outline:none` **uniquement** sur ce cas), navigation de semaine (`[` précédent, sélecteur `DatePickerButton`, `]` suivant, bouton « Aujourd'hui »), outils (`.toggle-panel`, `.m-tools` en mobile). Sur les pages « centrées semaine » (Temps, Envoi) : ligne `stepper + progression`, puis la **bande de jours** (§6.1) hors page Jour.
- **Progression** : `role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="{pct}" aria-label="Progression de la semaine"` + texte « 32 h / 40 h » (le texte, pas la barre, porte la valeur). Piste 140×8 px, rayon 999 ; piste `--border`, remplissage `--accent` (contraste du remplissage/piste 3:1 : `--accent` 4,88 vs `--surface` ✔). Atteint 100 % = remplissage `--ok` **et** icône `check` + texte « Objectif atteint ».
- **Navigation de semaine** : boutons avec `aria-label` (« Semaine précédente », « Semaine suivante ») et `title` incluant le raccourci ; annonce `aria-live="polite"` visuellement masquée du nouveau libellé de semaine après navigation.
- **Annonce de changement de page** : à chaque navigation, `document.title` mis à jour (`{Page} — Semaine {n} — Time Sheet Helper`) et focus sur `<h1>`.
- **Clavier** : `[` / `]` ; `Entrée` sur le sélecteur de date ouvre le sélecteur existant.

### 5.4 `StepIndicator` (`Layout\StepIndicator.razor`)

- **Structure** : `<ol class="stepper" aria-label="Parcours de la semaine">` ; 3 `<li>` — **1 Saisir** (→ Jour), **2 Vérifier** (→ Semaine), **3 Soumettre** (→ Soumettre) — séparés par `<li class="sep-arrow" aria-hidden="true">` (icône `chevR`). Chaque étape = `<a class="step …">` : pastille `.n` 22 px (numéro ou icône `check`), `.lbl`, `<small>` sous-titre d'état (« 2 jours à traiter », « tout envoyé », « 4 trous »).
- **États** (jamais la couleur seule) :

  | État | Classe | Pastille | Texte | Jetons | ARIA |
  | --- | --- | --- | --- | --- | --- |
  | à venir | (aucune) | numéro, anneau `--border-strong` | `--muted` | 4,93 / 5,77 | — |
  | courant | — | numéro plein `--accent`, texte `--on-accent` (5,19 / 6,11) | `--fg` gras + soulignement 2 px `--accent` | — | `aria-current="step"` |
  | terminé | `.done` | **icône `check`** sur `--ok-bg` | `--ok-text` | 6,45 / 5,98 | sous-titre texte (« tout envoyé ») |
  | attention | `.attn` | **icône `warn`** sur `--warn-bg` | `--warn-text` | 6,08 / 5,88 | sous-titre texte (« 2 jours à traiter ») |

- **Mobile (≤ 860 px)** : seules l'étape **courante** garde son libellé ; les autres n'affichent que la pastille (leur nom accessible reste fourni par `aria-label` = « Étape 2 : Vérifier, à traiter »).
- **Clavier** : liens ordinaires (Tab/Entrée) ; pas de piège.

### 5.5 `MobileNav` (barre basse + tiroir) (`Layout\MobileNav.razor`)

- **Barre basse** : `<nav class="bottom-bar" aria-label="Sections (mobile)">` ; **mêmes 4 liens que le rail** (mêmes icône/libellé/badge/`aria-current`), `flex:1`, hauteur 56 px (+ inset de sécurité), libellé 11 px visible en permanence ; actif = **filet haut 3 px `--accent`** + fond `--accent-bg` + texte `--accent-text` ; fond `--surface`, `border-top:1px solid var(--border)`, `z-index:70`. Cibles ≥ 44×44 ✔.
- **Tiroir** (panneau contextuel en mobile) : ouvert par un bouton « Menu » (icône `menu`, `aria-expanded`, `aria-controls="panel"`) placé dans l'en-tête ; `<aside class="panel drawer" role="dialog" aria-modal="true" aria-label="Panneau de section">` ; largeur `min(86vw, 320px)`, glisse depuis la gauche (`transform:translateX`, 180 ms ease ; sans animation en mouvement réduit) ; **scrim** `rgba(0,0,0,.4)` (`z-index:75`) fermant au clic ; `box-shadow: var(--shadow)` ; `z-index:80`.
  - **Focus** : à l'ouverture, focus sur le bouton de fermeture (en haut du tiroir) ; focus **piégé** dans le tiroir ; `Échap` ou activation d'un lien ferme et **rend le focus** au bouton « Menu » ; le reste de la page reçoit `inert`.
  - Le défilement du `body` est verrouillé tant que le tiroir est ouvert.
- **Sous-navigation** : `.subnav` (pastilles) sous l'en-tête pour passer d'une page à l'autre de la section sans ouvrir le tiroir ; pastille courante = `--accent-bg` + `--accent-text` + `aria-current="page"` + soulignement 2 px ; `overflow-x:auto`, sans barre de défilement visible (`scrollbar-width:none`), `scroll-snap`.
- **Outils** (`.m-tools`) : boutons Surveillance et Thème, mêmes états/`aria-*` que dans le rail.
- Bascule de `display` rail/barre pure CSS (point de rupture §4) ; **ne pas** dupliquer d'ids (les ids du rail et de la barre basse sont distincts).

---

## 6. Spécification des composants (Phase 2 — C+)

### 6.1 `DayStrip` (bande de jours)

- **Structure** : `<div class="daystrip" role="group" aria-label="État d'envoi par jour"><ul class="ds-list">` de 5 `<li><button class="ds-day t-{statut}" aria-pressed="{sélectionné}" aria-label="Mercredi 21 : Prêt, 8 h / 8 h, aujourd'hui">`. Contenu d'une carte : nom + n° (`Mer 21`) + étiquette « aujourd'hui » ; **pastille d'état** (`.ds-chip` = icône + libellé) ; heures « 8 / 8 h » (ou « – » si non saisi). Sous la liste : `.ds-actions` = bouton principal « Soumettre ce jour · Mer 21 » (libellé = `DayGate`, §7.2 ; sinon la raison du blocage), bouton « Soumettre les jours prêts (N) », lien « Voir le détail » (hors page Soumettre).
- **Disposition** : grille 5 colonnes, gap 8 px ; ≤ 860 px : flex défilant, cartes 132 px, `scroll-snap-type:x mandatory`, `scroll-padding` pour que la carte sélectionnée soit visible.
- **Statuts de jour** (11 : `DayStatus` des contrats) : voir matrice §7.2. Le bouton principal suit `DayGate` (table §7.2). Chaque carte porte un **filet haut 4 px** de la couleur du statut **en plus** de l'icône et du libellé (redondance, pas porteur de sens seul).
- **Sélection** : `aria-pressed="true"` = anneau 2 px `--accent` + fond `--accent-bg` + coche masquée visuellement (« sélectionné ») ; la sélection ne change **jamais** le libellé de statut.
- **Bouton principal** : désactivé par `disabled` quand le blocage est définitif, ou `aria-disabled="true"` quand l'envoi est en cours (reste focusable pour annoncer la raison) ; la raison est dans `title` **et** dans un texte `aria-describedby` visible sous le bouton (ne pas se limiter au `title`). Pendant l'envoi : libellé « Envoi en cours… 2/4 », spinner `.spin` (§9), second clic ignoré (verrou, garantie anti-doublon).
- **Clavier** : Tab entre cartes (un arrêt par carte) ; `Entrée`/`Espace` sélectionne ; flèches ←/→ **optionnelles** (roving tabindex) — par défaut Tab suffit, pas de piège. Annonce de la sélection via `aria-live="polite"` (« Mercredi 21 sélectionné, Prêt »).
- **Jetons** : texte `--fg` sur `--surface` (14,84 / 14,65) ; pastille = `--X-text` sur `--X-bg` (§2.3).

### 6.2 `DayPanel` (panneau du jour)

- **Structure** : `<section class="card" aria-labelledby="day-h">` ; `.card-head` : `<h2 id="day-h" class="sub-h">Mercredi 21 juillet (aujourd'hui)</h2>` + **badge de statut du jour** (icône + libellé, §7.2). Puis, dans l'ordre : alertes (§ ci-dessous), résumé `ul.sum` (« N à écrire · N déjà dans Forecast · N ignorée(s) · N non envoyée(s) »), tableau des cellules, actions (`Soumettre ce jour`, `Relire Forecast (simulation)`, `Annuler` pendant l'envoi), `DayJournal` (§6.5).
- **Tableau des cellules** : `<table>` avec `<caption class="visually-hidden">` ; colonnes : Tâche (`th scope="row"` + puce de couleur de tâche **décorative** `aria-hidden`), Brouillon, Forecast, Envoyé, **Résultat** (`CellStateBadge` + note + `ConflictChooser`), « Ne pas envoyer » (case). Nombres alignés à droite en chiffres tabulaires (`font-variant-numeric: tabular-nums`) avec unité dans l'en-tête (« h »).
- **Ligne par état** : filet gauche 3 px sur `th[scope=row]` (`--danger` pour supprimé/conflit, `--warn` pour modifié/non tenté) ; lignes ignoré/verrouillé/sans projet en `--muted`. Ces filets **doublent** le badge, ils ne le remplacent pas.
- **Alertes** (`.alert`, icône + texte en gras + texte `--X-text` sur `--X-bg`) : « Envoi partiel », « Modifié depuis l'envoi du … », « Conflit : Forecast a été modifié ailleurs ». **`role="alert"` uniquement** pour une erreur d'envoi qui vient de survenir (cf. décision §3.8 de `decisions.md`) ; les autres sont des `role="status"` (`aria-live="polite"`). Chacune commence par une icône (`warn`/`x`/`refresh`) et un mot-clé (« Conflit : », « Envoi partiel : »).
- **États vides** : jour `none` → « Aucune donnée pour ce jour (jour à venir). Rien à soumettre. » ; « Rien de nouveau à envoyer » (bouton désactivé + raison visible).
- **Garde-fous** (D6) : texte d'aide en `<details class="card">` « Comment fonctionne l'envoi » (pas de cartes de scénario) ; titre/légende repliable, `summary` focusable avec indicateur ▸/▾ **en SVG** (pas de caractère seul).
- **Jetons** : mêmes que §2.3 ; focus §8.

### 6.3 `CellStateBadge`

- **Structure** : `<span class="badge {ton}"><svg class="ico sm" aria-hidden="true">…</svg><span>Libellé</span></span>` ; **le libellé est du texte réel** (pas un `title`, pas un pseudo-élément). Jamais de badge sans icône ni sans texte.
- 8 états de cellule + « Écrit et vérifié » : matrice §7.1. Taille `.78rem`, hauteur ≥ 22 px, padding `1px 9px`, rayon 999 px, `white-space:nowrap` ; sur mobile le libellé peut passer à la ligne (`white-space:normal; border-radius:12px`).
- **Contraste** : `--X-text` sur `--X-bg`, **sur `--bg` comme sur `--surface`** (§2.3) ✔.

### 6.4 `ConflictChooser`

- **Structure** : pour chaque cellule en conflit ou supprimée localement, un `<fieldset class="res">` avec `<legend class="visually-hidden">Résolution pour « {tâche} »</legend>` et un contrôle `<select>` (comportement du prototype) **ou** groupe de boutons radio quand ≤ 4 options (préféré sur mobile). Options (libellés des contrats = `ConflictResolution`) : « **Garder** » (`Keep`, par défaut : ne rien modifier), « **Accepter Forecast** » (`AcceptForecast` : aligner le brouillon sur Forecast, X h), « **Compléter +Δ** » (`Complement`, si brouillon > Forecast : ajoute exactement D − F), « **Retirer** » (`Remove`, si supprimé localement ; **confirmation obligatoire**), chacune suivie d'une courte description (ex. « Aligner le brouillon sur Forecast (X h) »). Les options non disponibles (D3 : l'outil MCP n'existe pas) sont **affichées désactivées** avec la raison visible et le lien « Modifier dans Forecast » / « Voir dans Forecast », jamais masquées silencieusement.
- **Règles** : le choix par défaut ne change jamais Forecast (l'application n'écrase jamais) ; toute option destructive ouvre `ConfirmDialog` (focus initial sur **Annuler**, Échap annule, décision §3.8) ; un conflit **non résolu** désactive le bouton principal (`DayGate.ResolveConflicts`, « Écart à résoudre ») avec la raison « Choisissez comment traiter chaque conflit ». Le résultat choisi est répété en texte dans la note de la cellule (« Résolution : ajouter +1,25 h »).
- **Jetons** : bordure `--border-strong` (4,55 / 5,07 ✔) ; focus §8 ; icône `warn` en tête de la ligne de conflit.
- **Clavier** : Tab ; flèches pour les radios/`select` ; `Entrée` valide ; `Échap` ferme la confirmation.

### 6.5 `DayJournal` (journal d'envoi par jour)

- **Structure** : `<details class="card"><summary class="card-head">Journal d'envoi · {jour} (N entrées)</summary><ol class="journal" aria-label="Journal d'envoi du {jour}">` ; chaque `<li>` : heure (`<time datetime>`, tabulaire), **icône de ton** (`check`/`warn`/`x`/`refresh`/`plus`), action (« Soumettre ce jour », « Relire Forecast »), détail texte. Ordre chronologique décroissant.
- **Contenu** : aucune donnée sensible (pas de jeton, d'URL secrète, de corps de requête) — texte résumé uniquement (« Interrompu après 2/4 cellules — connexion Forecast perdue »).
- **ARIA** : région dynamique `aria-live="off"` (les annonces passent par le bandeau `role="status"`/`role="alert"` du `DayPanel`) ; focus : `summary` natif.
- **Jetons** : texte `--fg`, heure `--muted` ; icône = `--X`.

---

## 7. États : cellules et jours (matrice)

### 7.1 États de cellule (8 + 1)

| Clé | Libellé affiché (FR) | Icône | Ton / jeton | Note de ligne | Action(s) | `Soumettre` en tient compte |
| --- | --- | --- | --- | --- | --- | --- |
| `new` Nouveau | **Nouveau — sera écrit** | `plus` | `accent` (`--accent-text` sur `--accent-bg`) | « +X h ajoutés dans Forecast » | aucune | écrit |
| `same` Inchangé | **Déjà dans Forecast — ignoré (pas de doublon)** (si écrit pendant l'envoi : **Écrit et vérifié**, ton `ok`, icône `check`) | `check` | `info` (« Écrit et vérifié » : `ok`) | « heures identiques : aucune écriture » | aucune | n'écrit pas |
| `changed` Modifié | **Modifié depuis l'envoi** | `refresh` | `warn` | « Forecast X h → Y h : mise à jour de la cellule existante » ou « Forecast garde X h (mise à jour désactivée) » | option « mettre à jour » (D3) | écrit si activé |
| `deleted` Supprimé | **Supprimé localement** | `x` | `danger` | « Forecast garde X h : l'application ne supprime jamais seule » | `ConflictChooser` (supprimer = confirmation) | n'écrit pas |
| `conflict` Conflit | **Conflit — Forecast modifié ailleurs** | `warn` | `danger` | « Forecast X h ≠ brouillon Y h : cellule non modifiée » | `ConflictChooser` | **bloque** la journée |
| `ignored` Ignoré | **Ignoré (Ne pas envoyer)** | `minus` * | neutre (`--muted` sur `--surface`, bordure `--border-strong`) | « exclue de l'envoi pour la semaine » | case « Ne pas envoyer » | n'écrit pas |
| `locked` Verrouillé | **Verrouillé — non écrit** | `lock` * | `locked` (`--locked-text` sur `--locked-bg`) | « période approuvée / facturée dans Forecast » | aucune | n'écrit pas |
| `notattempted` Non tenté | **Non tenté (envoi interrompu)** | `clock` * | `warn` | « sera écrite au rejeu » | « Relire Forecast » puis rejouer | écrit au rejeu |

\* **Icônes à ajouter** au jeu d'icônes de l'app (même style : viewBox 24, trait 1,8 px, `round`) : `minus` = `<path d="M5 12h14"/>` ; `lock` = `<rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>` ; `clock` = `<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>` ; `menu` = `<path d="M4 6h16M4 12h16M4 18h16"/>` (si absent). **Formes tous distinctes** (plus, check, refresh, x, triangle, minus, cadenas, horloge) : un daltonien peut distinguer les 8 états sans la couleur.

> Écart assumé vs prototype : le prototype colore « Verrouillé » en accent ; la spec le passe en `--locked` (violet) pour ne pas le confondre avec « Nouveau » (accent). Et « Nouveau » passe en `accent` partout (le prototype a une surcharge `st-new` dans `submit.css` incohérente avec `badgeFor` = `ok`).

**Correspondance avec les contrats (§3 de `nav-c-contracts.md`)** — clés → enum `CellState` : `new`→`New`, `same`→`Unchanged`, `changed`→`Modified`, `deleted`→`Deleted`, `conflict`→`Conflict`, `ignored`→`Ignored`, `locked`→`Locked`, `notattempted`→`NotAttempted`. Les **libellés sont identiques** aux contrats. Seuls l'icône et le jeton diffèrent ; **la spec visuelle prévaut** (8 formes distinctes, état jamais porté par la couleur seule) :

| `CellState` | Contrats (icône / jeton) | Spec (icône / jeton) | Motif |
| --- | --- | --- | --- |
| `New` | `plus` / `--ok` | `plus` / `accent` | `ok` est réservé à « écrit et vérifié » ; évite la confusion avec `Unchanged` |
| `Unchanged` | `check` / `info` | identique | — |
| `Modified` | `refresh` / `warn` | identique | — |
| `Deleted` | `warn` / `danger` | `x` / `danger` | `warn` est déjà `Conflict` ; forme distincte |
| `Conflict` | `warn` / `danger` | identique | — |
| `Ignored` | aucune icône, texte barré + libellé / `--muted` | `minus` + libellé, nom de tâche barré / `--muted` | cohérence « un état = ton + icône + libellé » (§7.3) ; le barré des contrats est conservé |
| `Locked` | `warn` / `--locked` | `lock` / `--locked` | `warn` déjà pris ; le cadenas dit « verrouillé » |
| `NotAttempted` | `refresh` / `warn` | `clock` / `warn` | `refresh` est déjà `Modified` ; « en attente » = horloge |

`DayStateLabels.cs` (WP11, `Components\Shared`) doit porter les icônes et jetons de la colonne « Spec » (CR-6). Les libellés restent ceux des contrats.

### 7.2 Statuts de jour (DayStrip, badge du `DayPanel`, `.day-link`)

Enum `DayStatus` des contrats (§3) ; libellés = contrats. Le DayStrip affiche `NotAttempted` comme « Prêt » ou « Non saisi » selon `DayBadge` ; `Modified` = « Modifié depuis l'envoi ».

| Clé (spec) | `DayStatus` / `DayBadge` | Libellé (FR) | Icône | Jeton filet/pastille | Condition (rappel) | Compte dans « jours à traiter » |
| --- | --- | --- | --- | --- | --- | --- |
| `none` | `NotAttempted` (badge) | **Non saisi** | anneau vide (SVG) | neutre `--border-strong`, texte `--muted` | aucune heure / jour futur | non |
| `draft` | `NotAttempted` (badge) | **Brouillon** | `jour` | `info` | journée en cours sous la référence | non |
| `ready` | `NotAttempted` (badge) | **Prêt** | `soumettre` | `accent` | cellules à écrire | oui |
| `inprogress` | `InProgress` | **Envoi en cours…** | `refresh` (rotation §9) | `info` | verrou de jour actif | non |
| `sent` | `Sent` | **Envoyé** | `check` | `ok` | tout est dans Forecast | non |
| `sentunknown` | `SentUnknownFingerprint` | **Envoyé (empreinte inconnue)** | `check` | `ok` | envoi antérieur sans empreinte ; aide : « sera comparé à Forecast avant tout nouvel envoi » | non |
| `modified` | `Modified` | **Modifié depuis l'envoi** | `refresh` | `warn` | empreinte du brouillon ≠ empreinte envoyée | oui |
| `partial` | `Partial` | **Envoi partiel** | `warn` | `warn` | cellules écrites + non tentées | oui |
| `conflict` | `Conflict` | **Écart à résoudre** | `warn` | `danger` | Forecast ≠ envoyé (modifié hors de l'app), conflit non résolu | oui (bloque la journée) |
| `locked` | `Locked` | **Verrouillé** | `lock` | `locked` | période approuvée / facturée | non |
| `error` | `Error` | **Erreur d'envoi** | `warn` | `danger` | l'envoi vient d'échouer | oui |

D4 (contrats) : l'écart d'heures par rapport à la référence quotidienne (8 h, `Engine:TargetHoursPerDay`) est un **avertissement informatif seulement** : il ne produit **jamais** `DayGate.ResolveConflicts` ; l'objectif hebdomadaire (`NavSummary.WeeklyTargetHours`, 40 h) est une **mise en évidence non bloquante**. L'ancien statut `gap` de cette spec est donc **supprimé** ; « Écart à résoudre » désigne exclusivement un conflit Forecast (`Conflict`). Pour le simple écart d'heures : pastille `info` « Sous la référence »/« Au-dessus de la référence » (icône `jour`, jamais `danger`). Les statuts `partial` (`warn`), `conflict` et `error` (`danger`) diffèrent par le **libellé** ; l'icône `warn` identique est acceptable car le libellé est toujours visible.

**Bouton principal du jour (`DayGate`, libellés des contrats)** :

| `DayGate` | Libellé | État du bouton |
| --- | --- | --- |
| `Send` | Soumettre ce jour | actif |
| `SendChanges` | Envoyer les changements | actif, `ConfirmDialog` |
| `Resume` | Rejouer (écrit seulement ce qui manque) | actif |
| `InProgress` | Envoi en cours… n/N | `aria-disabled`, `aria-live="polite"` |
| `NothingToSend` | Rien à envoyer | désactivé + raison visible |
| `NothingNew` | Rien de nouveau à envoyer | désactivé + raison visible |
| `ResolveConflicts` | Écart à résoudre | désactivé + raison visible |
| `ReadForecastFirst` | Relire Forecast d'abord | **actif** (déclenche la lecture) |
| `Locked` | Verrouillé | désactivé + raison visible |

### 7.3 Règles transverses d'état

- Un état = **ton + icône + libellé** ; le libellé est dans le DOM même si l'icône est le seul visible sur une cellule très étroite (alors `visually-hidden` + `title`, jamais supprimé).
- Annonce : changement d'état (envoi en cours, terminé, interrompu) via **une seule** région `role="status"` (ou `role="alert"` pour l'échec) par page ; pas d'annonces en doublon.
- Impression/forcé-couleurs (`forced-colors: active`) : badges avec `border:1px solid CanvasText` ; icônes `currentColor` ; filets `border-left` gardés ; rien ne repose sur `background` seul.

---

## 8. Focus visible et interaction

- **Style unique** : `:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: inherit; }` — jamais `outline:none` sans substitut équivalent (sauf `<h1 tabindex="-1">` reçevant un focus programmatique, §5.3). Ratio du focus : `--accent` / `--bg` 5,19 / 6,11 ; / `--surface` 4,88 / 5,59 ; / `--accent-bg` ≥ 4,5 ✔ (≥ 3:1 exigé).
- Éléments **sur fond `--accent`** (bouton principal) : anneau `outline: 2px solid var(--fg); outline-offset: 2px` (fg/accent ≥ 3:1 ; sur `--bg` 15,8) pour rester visible sur le bouton.
- Dans le rail/barre basse : `outline-offset: -2px` (dans le lien) pour ne pas être coupé par `overflow`.
- **Skip link** : « Aller au contenu » (premier élément focusable, `.skip-link`, visible au focus, cible `#main`, `tabindex="-1"`).
- **Ordre de tabulation** : skip link → rail → panneau (si ouvert) → en-tête → bande de jours → contenu → barre basse (mobile, en dernier dans le DOM mais visuellement en bas).
- **Repères** : `<nav aria-label="Sections">`, `<aside aria-label="Panneau de section">`, `<header>`, `<main id="main">`, `<nav aria-label="Sections (mobile)">` — noms **uniques** par type de repère.
- **Cibles et survol** : états `:hover` jamais seuls porteurs d'information ; actions révélées au survol aussi disponibles au focus.
- **Zoom/redimensionnement** : 200 % sans perte de contenu ; reflow à 320 px.

---

## 9. Mouvement (`prefers-reduced-motion`)

| Élément | Normal | `prefers-reduced-motion: reduce` |
| --- | --- | --- |
| Repli du panneau (`grid-template-columns`) | `.18s ease` | aucune transition |
| Tiroir mobile (translateX) + scrim | 180 ms ease, fondu 120 ms | apparition **instantanée** |
| Survol/focus (fond, couleur) | `.12s` | aucune transition |
| Défilement (`scroll-behavior`, `scrollIntoView`) | `smooth` | `auto` |
| Spinner `.spin` (envoi) | rotation 0,8 s linéaire | **conservé mais ralenti** (rotation 2 s) ou remplacé par points statiques « … » : un indicateur d'attente doit rester perceptible (la règle actuelle `animation-duration:.01ms` de `app.css` le **fige** et rend l'attente invisible → à remplacer) |
| Barre de progression | `width .2s` | aucune transition |

- Bloc CSS de référence (remplace l'existant d'`app.css`, WP3) :
  ```css
  @media (prefers-reduced-motion: reduce) {
    *, *::before, *::after { transition: none !important; scroll-behavior: auto !important; }
    .spin { animation-duration: 2s !important; }      /* l'attente reste visible */
    :not(.spin) { animation-duration: .01ms !important; animation-iteration-count: 1 !important; }
  }
  ```
- Aucune animation > 3 s ni clignotante (WCAG 2.2.2 / 2.3.1). Aucune information portée par le seul mouvement.

---

## 10. Correspondance prototype → application

| Prototype (classe / id) | Application (Blazor) | Composant / fichier | Remarque |
| --- | --- | --- | --- |
| `.shell`, `.shell.no-panel` | `.app-shell`, `.app-shell.no-panel` | `MainLayout.razor(.css)` | grille §3 |
| `.rail`, `.rail-gap`, `.logo` | `.rail`, `.rail-gap`, `.rail-logo` | `Layout\Rail.razor` | `<nav>` |
| `.r-link` (+ `[data-tip]`) | `.rail-link` | `Rail.razor` | `aria-current` ; info-bulle CSS |
| `.r-foot`, `#track`, `#theme` | `.rail-foot`, `.rail-track`, `.rail-theme` | `Rail.razor` | `aria-pressed` |
| `.panel`, `.p-in`, `#panel` | `.ctx-panel`, `.ctx-panel-in` | `Layout\ContextPanel.razor` | repli `tsh.panel` |
| `.p-title`, `.p-sub`, `.p-link`, `.d` | `.ctx-title`, `.ctx-sub`, `.ctx-link`, `.ctx-link-desc` | `ContextPanel.razor` | |
| `.day-link`, `.dot.ok/.warn/.none`, `.today-tag` | `.ctx-day`, `.day-dot.ok/.warn/.none`, `.today-tag` | `ContextPanel.razor` | point = SVG + libellé |
| `.check.ok / .check.todo` | `.ctx-check.ok / .todo` | `ContextPanel.razor` | icône + texte |
| `.head`, `.head-in`, `.head-row`, `.kicker` | `.week-head`, `.week-head-in`, `.week-head-row`, `.kicker` | `Layout\WeekHeader.razor` | sticky z40 |
| `.toggle-panel`, `.icon-btn` | `.toggle-panel`, `.icon-btn` | `WeekHeader.razor` | |
| `.m-tools` | `.m-tools` | `WeekHeader.razor` | visible ≤ 860 px |
| `.stepper`, `.step(.done/.attn)`, `.n`, `.lbl`, `.sep-arrow` | `.stepper`, `.step(.done/.attn)`, `.step-n`, `.step-lbl`, `.step-sep` | `Layout\StepIndicator.razor` | |
| `.progress`, `.bar` | `.week-progress`, `.week-progress-bar` | `WeekHeader.razor` | `role=progressbar` |
| `.subnav` | `.subnav` | `Layout\MobileNav.razor` | pastilles |
| `.bottom-bar` | `.bottom-bar` | `Layout\MobileNav.razor` | z70 |
| `.daystrip`, `.ds-list`, `.ds-day.t-*`, `.ds-chip`, `.ds-h`, `.ds-actions` | `.day-strip`, `.day-strip-list`, `.day-card.t-*`, `.day-chip`, `.day-hours`, `.day-strip-actions` | `DayStrip.razor` (WP11 ; dossier à fixer par WP11, les contrats ne le précisent pas — `Components\Shared` proposé, avec `DayStateLabels.cs`) | |
| `.card`, `.card-head`, `.sub-h`, `.sum` | `.card`, `.card-head`, `.sub-h`, `.sum` | `DayPanel.razor` | `.card` déjà proche de l'existant |
| `tr.st-new/same/changed/deleted/conflict/ignored/locked/notattempted` | `tr.cell-new/…/cell-notattempted` | `DayPanel.razor` | préfixe `cell-` (évite `st-`) |
| `.badge.ok/warn/danger/info/accent` | `.badge.ok/…/accent` + `.badge.locked`, `.badge.neutral` | `CellStateBadge.razor` | `--X-text`/`--X-bg` |
| `td.res-cell`, `.rnote`, `label.res` | `.cell-result`, `.cell-note`, `.conflict-choice` | `ConflictChooser.razor` | |
| `.alert(.warn/.danger)` | `.alert(.warn/.danger/.info)` | `DayPanel.razor` | icône + `role` |
| `.spin`, `@keyframes sub-spin` | `.spin`, `@keyframes spin` | `app.css` | §9 |
| `.visually-hidden`, `.skip-link`, `.muted`, `.num` | identiques | `app.css` | déjà présents |
| `html[data-theme]` / `prefers-color-scheme` | idem (§2.1) | `app.css` + `shell.js` | |
| `localStorage "tsh-proto-c-panel"` | `localStorage "tsh.panel"` / `"tsh.theme"` | `shell.js` | |
| Cartes de scénario / légende / garde-fous pédagogiques | **exclus** (D6) ; garde-fous → `<details>` d'aide | `Submit.razor` | |
| `.app-nav` (barre actuelle) | **supprimée** | `MainLayout.razor` | remplacée par le rail |

Les noms de classes CSS de la colonne « Application » sont la **proposition de WP2, validée** : `nav-c-contracts.md` n'impose aucun nom de classe CSS. Les composants Layout (`Components\Layout\{Rail,ContextPanel,WeekHeader,StepIndicator,MobileNav,PageHeader}.razor`, `ShortcutMap.cs`, `ShellState.cs`) et les clés `tsh.theme` (`"light"`/`"dark"`) / `tsh.panel` (`"1"`/`"0"`) sont ceux des contrats. `PageHeader(Title, Actions)` alimente le `h1` de `WeekHeader`. Tout renommage ultérieur par WP3 met ce tableau à jour (CR-3).

---

## 11. Captures de référence (WP7 = Phase 1, WP15 = Phase 2)

Dossier : `docs\plans\screenshots\p1\` et `docs\plans\screenshots\p2\`. Nom : `{page}-{largeur}-{theme}.png` avec largeur ∈ {`1440x900`, `390x844`} et thème ∈ {`clair`, `sombre`}.

### 11.1 Phase 1 — 7 pages × 2 thèmes × 2 gabarits = **28 captures** (`screenshots\p1\`)

| Page | Route | Fichiers (extrait) |
| --- | --- | --- |
| Semaine | `/` | `semaine-1440x900-clair.png`, `semaine-1440x900-sombre.png`, `semaine-390x844-clair.png`, `semaine-390x844-sombre.png` |
| Jour | `/jour` | `jour-…` (4) |
| Soumettre | `/soumettre` | `soumettre-…` (4) |
| Règles | `/regles` | `regles-…` (4) |
| Tâches | `/taches` | `taches-…` (4) |
| Sources | `/sources` | `sources-…` (4) |
| Activité brute | `/activite` | `activite-…` (4) |

Captures complémentaires Phase 1 (état d'interface) : `tiroir-390x844-clair.png`, `tiroir-390x844-sombre.png` (tiroir ouvert), `panneau-replie-1440x900-clair.png` (Ctrl+B), `focus-rail-1440x900-clair.png`, `focus-rail-1440x900-sombre.png` (focus clavier visible sur un lien du rail), `infobulle-rail-1440x900-clair.png`.

Vérifications associées : rail/panneau/en-tête présents ; Alt+T bascule le thème **sans flash** ; Alt+1..7 ; `[` `]` ; Ctrl+B ; tiroir (focus piégé, Échap) ; aucun débordement horizontal à 390 px (et 320 px) ; console sans erreur ; `prefers-reduced-motion` (émulé) : panneau/tiroir sans transition, spinner visible mais lent.

### 11.2 Phase 2 — page Soumettre, 6 scénarios × 2 thèmes × 2 gabarits = **24 captures** (`screenshots\p2\`)

Scénarios (jeux de données de démonstration du prototype, fake local — jamais le vrai Forecast) : `neuf`, `inchange`, `modifie`, `conflit`, `partielle`, `double`. Nom : `soumettre-{scenario}-{largeur}-{theme}.png`. Ce que chaque capture doit **montrer** :

| Scénario | À montrer (couleur + icône + libellé) |
| --- | --- |
| `neuf` | jour « Prêt », cellules « Nouveau — sera écrit », « Soumettre les jours prêts (2) », cellule « Ignoré » |
| `inchange` | jours « Envoyé », bouton désactivé « Rien de nouveau à envoyer » + raison visible |
| `modifie` | badge « Modifié depuis l'envoi », cellules modifié / supprimé / nouveau, alerte d'empreinte |
| `conflit` | cellule « Conflit », « Verrouillé — non écrit », `ConflictChooser` ouvert avec options (dont indisponibles), bouton bloqué avec raison |
| `partielle` | alerte « Envoi partiel », « Écrit et vérifié » + « Non tenté », bouton « Relire Forecast d'abord » |
| `double` | envoi en cours (« Envoi en cours… 2/4 », spinner), bouton `aria-disabled`, journal avec « second clic refusé » |

Captures complémentaires : `daystrip-6statuts-1440x900-{clair,sombre}.png` (les 6 statuts de jour côte à côte, cf. §7.2), `daystrip-390x844-{clair,sombre}.png` (défilement horizontal), `journal-ouvert-1440x900-clair.png`, `confirm-suppression-1440x900-{clair,sombre}.png` (focus initial sur **Annuler**), `mouvement-reduit-envoi-1440x900-clair.png` (spinner présent).

**Critère de relecture des captures** : pour chacune des 8 cellules et 6 statuts de jour, l'état est identifiable **en niveaux de gris** (icône + libellé lisibles).

---

## 12. Check-list d'acceptation design (pour WP3/WP6/WP7/WP11/WP15)

- [ ] Les trois blocs de thème du §2.1 existent dans `app.css` ; plus aucune redéfinition locale basée sur `@media` seul des jetons partagés.
- [ ] Tout texte de badge/alerte utilise `--X-text` ; `--muted` jamais sur une teinte posée sur `--surface`.
- [ ] `--on-accent` utilisé sur tout fond `--accent` plein (sombre : `#0d1117`).
- [ ] 8 états de cellule + 7 statuts de jour = icône + libellé + ton, conformes aux §7.1/§7.2 ; libellés FR identiques.
- [ ] Focus visible sur 100 % des contrôles (clair et sombre) ; skip link ; `<h1>` focalisé à la navigation.
- [ ] Barre basse ≤ 860 px, tiroir avec piège de focus et retour de focus, aucun débordement à 320/390 px.
- [ ] `prefers-reduced-motion` : aucune transition ; spinner conservé et ralenti.
- [ ] 28 captures P1 + 24 captures P2 (+ complémentaires) produites et comparées au prototype.

---

## 13. Demandes de changement et risques

| # | Destinataire | Demande |
| --- | --- | --- |
| CR-1 | WP3 (Cendrillon, `app.css`) | Ajouter `data-theme` (3 blocs §2.1), hisser/ajouter les jetons §2.2–2.3 (`--surface`, `--accent-bg`, `--on-accent`, `--border-strong`, `--shadow`, `--radius`, `--X-text`), remplacer le bloc `prefers-reduced-motion` (§9). |
| CR-2 | WP6 / propriétaires de pages | Supprimer les redéfinitions locales de jetons partagés dans `Day/Week/ForecastTasks/Rules/Sources.razor.css` ; double sélecteur pour les jetons locaux ; renommer l'`--info-bg` bleu local (collision §2.4). |
| CR-3 | WP3 | **Résolu par les contrats** pour les noms de composants et clés `localStorage` ; reste : confirmer les noms de classes CSS du §10 (non imposés) ; 4 sections de rail, 7 destinations dans le panneau. |
| CR-4 | WP3 / WP11 | Ajouter les icônes `minus`, `lock`, `clock`, `menu` au jeu d'icônes (§7.1). |
| CR-5 | WP1 / WP10 | **Résolu par les contrats (D4)** : écart d'heures quotidien = informatif, jamais `ResolveConflicts` ; 40 h hebdo non bloquant. Le statut `gap` est supprimé (§7.2). |
| CR-6 | WP11 (`DayStateLabels.cs`) / WP12 | Icônes/jetons des 8 `CellState` selon la colonne « Spec » du §7.1 (écarts visuels assumés vs contrats §3 : `New`, `Deleted`, `Ignored`, `Locked`, `NotAttempted`) ; statuts de jour du §7.2 (dont `Partial` = ton `warn`, `Conflict`/`Error` = `danger`). Les tests de libellés ne doivent pas figer les icônes des contrats. |

**Risques restants** : (1) l'état de contraste a été calculé sur les valeurs spécifiées ; le rendu réel (anti-aliasing, profil couleur) peut différer légèrement — la vérification Playwright de WP7/WP15 doit confirmer visuellement ; (2) une page peu mise à jour qui garde un `@media` local sans `data-theme` reproduira le défaut « thème forcé ignoré » (CR-2) ; (3) l'ordre de tabulation barre basse/tiroir dépend de l'implémentation Blazor (focus piégé à tester) ; (4) icône `warn` partagée par `conflict`, `partial`, `error` (jour) et `conflict` (cellule) — la différenciation repose sur le libellé, toujours affiché.
