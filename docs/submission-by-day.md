# Soumission jour par jour

Page **Soumettre** (Alt+3), section « Soumission jour par jour ». Chaque jour de la semaine s'envoie vers Forecast indépendamment des autres, sans jamais créer de doublon ni écraser une saisie faite dans Forecast.

## Parcours

1. **Bande des jours** : un jour par carte, avec son état, ses heures et l'objectif du jour. Sélectionner un jour ouvre son panneau.
2. **Panneau du jour** : résumé (« N à écrire · N déjà dans Forecast · N ignorée · N non envoyée »), une ligne par cellule (tâche) avec ce que l'envoi en fera, puis le journal du jour.
3. **Bouton du jour** : « Soumettre ce jour ». Avant d'écrire, Forecast est relu ; le bouton est désactivé avec sa raison toujours visible quand rien ne peut être écrit.
4. **« Soumettre les jours prêts (N) »** : envoie d'un coup les jours prêts de la semaine après relecture de Forecast. Une confirmation liste les jours envoyés et ceux qui ne le sont pas, avec la raison. Les conflits ne sont jamais résolus à cet endroit : un jour en conflit est sauté.

## États d'un jour

| État | Signification | Action proposée |
|---|---|---|
| Non saisi | Aucune heure dans le brouillon | Rien à envoyer |
| Prêt | Heures dans le brouillon, jamais envoyées | Soumettre ce jour |
| Envoi en cours… | Un envoi tourne (un seul à la fois par jour) | Attendre |
| Envoyé | Écrit et relu dans Forecast, brouillon inchangé | Rien de nouveau à envoyer |
| Envoyé (empreinte inconnue) | Semaine envoyée avant cette version (reprise de l'historique) | Comparé à Forecast avant tout nouvel envoi |
| Modifié depuis l'envoi | Le brouillon a changé depuis l'envoi | Envoyer les changements (confirmation) |
| Envoi partiel | Envoi interrompu, annulé ou planté | Rejouer : relit Forecast, n'écrit que ce qui manque |
| Écart à résoudre | Forecast a été modifié ailleurs | Choisir un traitement par conflit |
| Erreur d'envoi | L'envoi a échoué | Vérifier le jour puis relancer |
| À relire | La lecture de Forecast était incomplète | Relire Forecast d'abord (rien n'est écrit) |

Un état n'est jamais porté par la couleur seule : chaque état a un libellé et une icône.

## Résoudre un conflit

Pour chaque cellule en conflit, quatre choix. Les choix indisponibles restent visibles, désactivés, avec leur raison.

- **Garder** : rien n'est modifié dans Forecast.
- **Accepter Forecast** : le brouillon est aligné sur les heures de Forecast.
- **Compléter +Δ** : ajoute exactement la différence (brouillon − Forecast) ; seulement si le brouillon dépasse Forecast.
- **Retirer** : retire la saisie de Forecast.

Compléter et Retirer modifient Forecast : une confirmation est demandée, et la modification est faite à l'envoi suivant du jour, après une nouvelle lecture de Forecast. Si Forecast ne permet pas de savoir à quelle tâche appartiennent des heures, aucun choix d'écriture n'est offert : modifiez-les dans Forecast.

## Garanties (G1 à G12)

| # | Garantie | Tests |
|---|---|---|
| G1 | Un second envoi d'un jour inchangé n'écrit rien | `DaySubmissionIdempotenceTests.G1_*`, `DayStateLabelsTests.G1_*` |
| G2 | Double clic ou deux onglets : une seule écriture | `DayLockTests.G2_ConcurrentSends_SingleWrite` |
| G3 | Envoi interrompu, annulé ou planté : le rejeu relit Forecast et n'écrit que le manquant | `Idempotence G3_*` |
| G4 | Lecture incomplète de Forecast : rien n'est écrit | `G4_IncompleteRead_WritesNothing`, `DayStateLabelsTests.G4_*` |
| G5 | Jour modifié : mise à jour de la saisie existante, jamais une seconde saisie ni une saisie négative | `G5_*` |
| G6 | Un conflit n'est jamais écrasé sans choix explicite ; Compléter = delta | `ConflictResolutionTests.G6_*`, `DayStateLabelsTests.G6_*` |
| G7 | Aucune notion de verrou : un jour approuvé ou facturé dans Forecast reste envoyable comme les autres | `ApprovedOrInvoicedDay_IsStillSent`, `DayStateLabelsTests.Labels_ApprovedDay_CanStillBeSent` |
| G8 | Une tâche ignorée n'est jamais écrite (ni supprimée, ni réécrite si ignorée après envoi) | `G8_*` |
| G9 | « Soumettre les jours prêts » n'envoie que les jours prêts, de façon idempotente | `SendReadyDaysTests.G9_*`, `DayStripWiringTests.G9_*` |
| G10 | Empreinte du jour stable (ordre, culture) et sensible aux heures, tâches et jour | `DayFingerprintTests.G10_*` |
| G11 | Une semaine déjà envoyée n'est jamais reproposée comme nouvelle après migration | `DaySubmissionMigrationTests.G11_*`, `Idempotence G11_*` |
| G12 | Le journal ne contient ni secret, ni jeton, ni corps de courriel | Contrat de conception (voir ci-dessous) ; pas de test nommé G12 |

Tests dans `tests\TimeSheetHelper.Tests` : `dotnet test tests\TimeSheetHelper.Tests`.

## Journal

Chaque jour garde un journal : « Soumettre ce jour », « Saisie créée », « Saisie modifiée », « Saisie retirée », « Rien d'écrit », « Conflit résolu », « Envoi interrompu », « Rejeu », « Envoi annulé », « Échec », « Reprise de l'historique ». Les messages sont des textes français fixes qui ne portent que des nombres, des dates et des identifiants de tâche (500 caractères au plus).

## Limites

- **Modification et suppression** : le serveur forecast-mcp n'offre pas d'outil de modification ni de suppression d'heures. Tant que ces outils ne sont pas détectés (ou forcés par `Forecast:Tools:UpdateTimeEntry` et `Forecast:Tools:DeleteTimeEntry`), les cellules Modifié, Supprimé, Compléter et Retirer sont bloquées avec « modifiez-la dans Forecast ». L'application ne crée jamais de saisie de remplacement ni de saisie négative pour contourner.
- **Approbation** : l'envoi de la semaine pour approbation n'est pas automatisé ; soumettez-la vous-même dans Forecast.
- **Teams** : lire les discussions Teams demande la permission Graph `Chat.Read` (ajoutée dans Entra, consentement administrateur, puis reconnexion). `ChatMessage.Read` ne suffit pas. Voir [microsoft365-setup.md](microsoft365-setup.md).
