# v2 — L'assistant courrier (conception courte)

*22 novembre 2026. Retour d'un avocat utilisateur (5 messages vocaux) : le besoin prioritaire est de
remplacer le secrétariat, en commençant par la veille des e-mails.*

## Ce que veut l'avocat (ses mots, résumés)

- Une veille des e-mails reçus : l'outil comprend **quel dossier** est concerné, dit **en une phrase ce
  qu'il faut faire**, et **propose une réponse** selon l'état d'avancement du dossier, avec toujours
  **la validation finale** de l'avocat.
- À terme : comprendre la vie du dossier (audience dans deux mois, commentaires client attendus),
  relancer clients et impayés, ouvrir un dossier à partir d'une note de rendez-vous scannée.
- Le suivi du temps est apprécié mais secondaire : son cabinet facture surtout au forfait.

## Première version (v2.0) — choix de Claude

- **IA locale via Ollama** (logiciel gratuit, Mac et Windows), appelée uniquement sur
  `127.0.0.1`. Aucun e-mail n'est envoyé à une entreprise d'IA. Modèle par défaut selon la mémoire :
  ≥ 32 Go → `mistral-small3.2` ; ≥ 16 Go → `gemma3:12b` ; sinon `gemma3:4b` (réglable).
- **Entrée** : l'avocat dépose un e-mail (fichier `.eml` ou `.msg` glissé depuis Outlook) ou le colle.
  La lecture automatique de la boîte viendra ensuite (Microsoft 365 / COM Outlook / IMAP, selon le
  cabinet).
- **Analyse** : dossier (mots-clés et adresses connues), résumé en une phrase, action à mener en une
  phrase, urgence, échéance éventuelle, brouillon de réponse en français soutenu.
- **Contexte du dossier** : une fiche « Où en est le dossier » saisie par l'avocat (en attendant
  Kleos) et les résumés des derniers e-mails du dossier.
- **Fenêtre « Courrier »** : liste des e-mails analysés, détail, brouillon modifiable, « Copier la
  réponse », « Marquer comme traité », changement de dossier. Rien n'est envoyé par l'outil.
- **Confidentialité** : e-mails et analyses chiffrés sur le poste, comme le reste. Seul l'assemblage
  `Cabinet.Ai` peut ouvrir une connexion, et uniquement vers la machine elle-même (vérifié par test).
