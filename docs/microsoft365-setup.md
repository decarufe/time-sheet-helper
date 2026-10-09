# Configuration Microsoft 365 (calendrier, courriels envoyés, Teams)

Time Sheet Helper lit vos rendez-vous, les métadonnées de vos courriels envoyés et vos messages Teams via
Microsoft Graph, **en votre nom** (permissions déléguées). Seules les métadonnées sont conservées
(objet, heure, participants) : le corps des courriels et des messages n'est jamais lu ni stocké.

Pour cela, il faut une *inscription d'application* (app registration) dans Entra ID (anciennement Azure AD).

## 1. Créer l'inscription d'application

1. Ouvrez le portail Entra : <https://entra.microsoft.com> → **Identité** → **Applications** → **Inscriptions d'applications**.
2. Cliquez sur **Nouvelle inscription**.
   - **Nom** : `Time Sheet Helper` (ou un nom de votre choix).
   - **Types de comptes pris en charge** : *Comptes dans cet annuaire organisationnel uniquement* (locataire unique).
   - **URI de redirection** : choisissez la plateforme **Client public/natif (mobile et bureau)** et saisissez `http://localhost`.
3. Cliquez sur **Inscrire**.
4. Sur la page **Vue d'ensemble**, notez :
   - **ID d'application (client)** → `ClientId`
   - **ID de l'annuaire (locataire)** → `TenantId`

## 2. Configurer l'authentification

Dans **Authentification** :

1. Sous **Applications mobiles et de bureau**, vérifiez que `http://localhost` est présent.
2. Ajoutez aussi l'URI du répartiteur Windows (WAM), utilisée pour l'authentification unique sur un poste joint à Entra ID :
   `ms-appx-web://microsoft.aad.brokerplugin/<ClientId>` (remplacez `<ClientId>` par l'ID d'application).
3. Sous **Paramètres avancés**, mettez **Autoriser les flux clients publics** à **Oui**.
4. Enregistrez.

> Aucun secret client ni certificat n'est nécessaire : il s'agit d'une application cliente publique.

## 3. Ajouter les permissions déléguées

Dans **Autorisations des API** → **Ajouter une autorisation** → **Microsoft Graph** → **Autorisations déléguées**, ajoutez :

| Permission         | Utilisation                                                  |
|--------------------|--------------------------------------------------------------|
| `User.Read`        | Identifier l'utilisateur connecté (id, domaine)              |
| `Calendars.Read`   | Lire les rendez-vous (`/me/calendarView`)                    |
| `Mail.ReadBasic`   | Lire les métadonnées des courriels envoyés, sans le corps    |
| `Chat.ReadBasic`   | Lister les conversations Teams et leurs membres              |
| `ChatMessage.Read` | Lire les messages de conversation (seules l'heure et l'auteur sont utilisés) |

## 4. Consentement administrateur

Dans un locataire d'entreprise, les utilisateurs ne peuvent souvent pas consentir eux-mêmes à `Calendars.Read`,
`Chat.ReadBasic` ou `ChatMessage.Read`. Si la connexion affiche « Approbation de l'administrateur requise » :

- si vous êtes administrateur : dans **Autorisations des API**, cliquez sur **Accorder un consentement administrateur pour &lt;votre organisation&gt;** ;
- sinon, voir la section [Demander le consentement au service informatique](#6-demander-le-consentement-au-service-informatique).

## 5. Renseigner appsettings.json

Ajoutez une section `Microsoft365` dans `appsettings.json` (à côté de l'exécutable `TimeSheetHelper.exe`) :

```json
{
  "Microsoft365": {
    "TenantId": "00000000-0000-0000-0000-000000000000",
    "ClientId": "11111111-1111-1111-1111-111111111111",
    "UseBroker": true,
    "MaxChats": 50,
    "InternalDomains": [ "contoso.com", "contoso-labs.com" ]
  }
}
```

| Clé               | Description |
|-------------------|-------------|
| `TenantId`        | ID de l'annuaire (locataire), ou son domaine (`contoso.onmicrosoft.com`). |
| `ClientId`        | ID d'application (client). |
| `UseBroker`       | `true` (par défaut) : utilise le répartiteur Windows (WAM) pour l'authentification unique avec le compte Windows. `false` : ouvre le navigateur système sur `http://localhost`. |
| `MaxChats`        | Nombre maximal de conversations Teams analysées par synchronisation (les plus récentes d'abord). |
| `InternalDomains` | Domaines considérés comme internes pour déterminer le client (domaine externe dominant) d'un courriel ou d'une réunion. Votre propre domaine est toujours interne. |

Ensuite, depuis l'interface de Time Sheet Helper, cliquez sur **Se connecter à Microsoft 365**. Sur un poste joint
à Entra ID, la connexion est généralement silencieuse ; sinon, une fenêtre de connexion Microsoft s'ouvre.

Le jeton est conservé dans `%LOCALAPPDATA%\TimeSheetHelper\msal-microsoft365.cache`, chiffré avec DPAPI
(lisible uniquement par votre compte Windows). Supprimez ce fichier pour forcer une nouvelle connexion.

## 6. Demander le consentement au service informatique

Si vous n'avez pas les droits pour créer une inscription d'application ou accorder le consentement,
envoyez une demande à votre service informatique avec ces informations :

- **Type d'application** : client public (application de bureau), sans secret, permissions **déléguées** uniquement
  (l'application ne voit que les données de l'utilisateur connecté).
- **URI de redirection** : `http://localhost` et `ms-appx-web://microsoft.aad.brokerplugin/<ClientId>`.
- **Permissions Microsoft Graph déléguées** : `User.Read`, `Calendars.Read`, `Mail.ReadBasic`, `Chat.ReadBasic`, `ChatMessage.Read`.
- **Données conservées** : localement sur le poste, dans une base SQLite ; uniquement les métadonnées (heure, objet,
  participants) — jamais le corps des courriels ni des messages Teams.
- **Ce qu'il faut en retour** : l'`ID d'application (client)`, l'`ID de l'annuaire (locataire)` et le consentement
  administrateur accordé pour ces permissions.

Le service informatique peut aussi accorder le consentement directement via l'URL :

```
https://login.microsoftonline.com/<TenantId>/adminconsent?client_id=<ClientId>
```

## Dépannage

| Symptôme | Cause probable |
|----------|----------------|
| « Inscription d'application Microsoft 365 manquante » | `TenantId` ou `ClientId` absent de `appsettings.json`. |
| « Connexion Microsoft 365 requise » | Pas encore connecté, ou le jeton a expiré : reconnectez-vous depuis l'interface. |
| `AADSTS65001` / approbation requise | Consentement administrateur manquant (section 4). |
| `AADSTS50011` (URI de redirection) | L'URI `http://localhost` ou celle du répartiteur WAM n'est pas inscrite (section 2). Vous pouvez aussi mettre `UseBroker` à `false`. |
| `AADSTS7000218` | « Autoriser les flux clients publics » n'est pas activé (section 2). |
