# Outil de cabinet — Étape 1 : les temps automatiques

*Document de conception — 22 novembre 2026*

## 1. Intention

Un avocat (utilisateur unique pendant le pilote) perd chaque soir un temps important à
reconstituer de mémoire et saisir dans Kleos ses temps passés sur 4 à 5 dossiers.
Ses documents arrivent par e-mail ; il crée ensuite le dossier et saisit ses temps en fin
de journée. Il juge ce travail « long et pénible ».

Il veut un outil personnel, installé sur son poste Windows, qui :

- observe son activité sur l'ordinateur et la rattache aux bons dossiers ;
- range automatiquement les pièces jointes reçues par e-mail ;
- lui présente chaque soir un récapitulatif des temps prêt à relire, corriger et reporter dans Kleos.

**Contrainte absolue : le secret professionnel.** Aucune entreprise (éditeur, fournisseur
d'IA, hébergeur) ne doit avoir un moyen d'accéder aux informations sur ses clients. Tout
reste sur son ordinateur.

**Pilote personnel.** S'il en tire un bénéfice net, il le proposera à ses collègues
avocats. La conception ne doit pas l'empêcher plus tard, mais l'étape 1 est mono-utilisateur.

**Critère de succès.** La saisie des temps du soir passe d'une corvée de reconstitution
à environ deux minutes de relecture, avec des durées assez justes pour être facturées sans
retouche importante.

**Suite prévue (hors de ce document) : étape 2, l'instruction.** Une IA locale aidera à
résumer les pièces, établir des chronologies, répondre à des questions sur un dossier et
préparer des premiers jets à partir de ses modèles. L'étape 1 doit préparer le terrain
(pièces rangées par dossier sur le disque), sans l'implémenter.

## 2. Contexte de l'utilisateur

- **Poste** : Windows (10 ou 11, x64), géré par l'utilisateur lui-même (pas de prestataire
  informatique, pas de bureau à distance). Outlook et Word tournent directement sur le poste.
- **Messagerie** : Outlook **classique** (avec menu Fichier), boîte **personnelle** (non partagée).
- **Fichiers de dossiers** : sur le disque de l'ordinateur. Organisation actuelle des dossiers
  (un répertoire par affaire ou non) non précisée : l'outil doit pouvoir se brancher sur une
  arborescence existante ou en créer une.
- **Volume** : environ **30 dossiers ouverts**. Possibilité d'export depuis Kleos inconnue.
- **Agenda** : audiences et rendez-vous présents dans Kleos **et** dans Outlook. L'outil lit Outlook uniquement.
- **Logiciel de gestion** : Kleos (Wolters Kluwer), en ligne, utilisé dans le navigateur. Il reste
  la référence pour les dossiers, la facturation et le RPVA. L'utilisateur ne veut pas le quitter
  pour l'instant. Pas d'API Kleos (option payante, à envisager seulement si le pilote convainc) :
  les temps validés sont **reportés à la main** dans Kleos, l'outil facilitant la copie.
- **Rédaction** : Word ; pièces en PDF.

## 3. Objectifs (étape 1)

1. **La pastille** : petit widget toujours visible, qui s'ouvre en panneau au clic (§ 6.1).
2. **Le suivi d'activité** : enregistrement continu et local de la fenêtre active (§ 6.2).
3. **Le rattachement aux dossiers** : chaque moment d'activité est attribué à un dossier, ou signalé comme non reconnu (§ 6.3).
4. **Outlook** : lecture des e-mails reçus, rangement des pièces jointes dans le dossier, lecture de l'agenda (§ 6.4).
5. **Le récapitulatif de fin de journée** : relecture, correction, validation et copie vers Kleos (§ 6.5).
6. **Une liste de dossiers locale** minimale, nécessaire au rattachement (§ 6.6).
7. **Les garde-fous de confidentialité** (§ 7).

## 4. Non-objectifs (étape 1)

- Remplacer Kleos ou écrire dans Kleos.
- L'IA et l'analyse de documents (résumés, questions, détection d'échéances dans les pièces) : c'est l'étape 2.
  Le champ « Poser une question » de la maquette est **masqué** à l'étape 1.
- Le RPVA.
- La facturation (elle reste dans Kleos).
- Plusieurs utilisateurs, un serveur ou une synchronisation entre postes.
- Le Mac.
- Envoyer, déplacer, supprimer ou modifier quoi que ce soit dans Outlook. L'accès est en **lecture seule**.

## 5. Anti-objectifs (échec même si « ça marche »)

- **Une donnée client sort du poste**, quelle que soit la voie : réseau, télémétrie, rapport
  de plantage, mise à jour automatique, synchronisation cloud. Les titres de fenêtres, noms de
  fichiers et objets d'e-mails **sont** des données client.
- **L'outil devient une corvée de plus** : trop de notifications, trop de confirmations, une
  pastille qui gêne.
- **Outlook ou le PC ralentit**, ou Outlook affiche des alertes de sécurité à répétition.
- **Des temps validés sont perdus.**
- **Une mauvaise attribution passe inaperçue.** Ce qui est incertain doit être signalé, jamais deviné en silence.
- **Un temps est compté sans validation.** Rien n'est considéré comme définitif sans validation explicite.

## 6. Fonctionnement

### 6.1 La pastille (option A retenue)

Maquette de référence : `maquettes/widget-1.html`, option A.

**État replié.** Une bulle arrondie, toujours au premier plan, en bas à droite au-dessus de la
barre des tâches par défaut. Elle est déplaçable et mémorise sa position. Elle ne prend jamais
le focus clavier. Elle affiche :

- une pastille de couleur : vert = enregistrement, gris = dossier non reconnu, ambre = en pause ;
- le nom court du dossier en cours, ou « Dossier ? » ;
- le chrono du segment en cours sur ce dossier ;
- un bouton pause ;
- le total du jour.

**État ouvert (au clic).** Un panneau ancré sur la pastille, qui se referme quand on clique ailleurs :

- **En cours** : dossier, chrono, boutons *Pause* et *Changer de dossier*. Ce dernier ouvre une
  recherche rapide dans la liste des dossiers et force le rattachement du moment présent.
- **Aujourd'hui** : total par dossier, total général, bouton *Valider ma journée* qui ouvre le récapitulatif (§ 6.5).
- **À venir** : événements Outlook des 7 prochains jours (audiences, rendez-vous), avec une étiquette de délai (« demain 9 h 30 »).
- **Courrier** : nombre d'e-mails rangés aujourd'hui et nombre « à vérifier », avec un lien vers la liste.

**Icône dans la zone de notification** : ouvrir, pause, réglages, quitter.

**Rappel du soir** : à une heure réglable (18 h 30 par défaut), la pastille change d'aspect
(halo discret) pour inviter à valider la journée. Pas de fenêtre surgissante.

**Style** : sobre, dans l'esprit de Windows 11 et de la maquette (fond clair, accent bleu
encre, typographie Segoe UI), en français. Le confort visuel est une exigence explicite de
l'utilisateur (« plaisant à utiliser »).

### 6.2 Le suivi d'activité

- L'outil enregistre la **fenêtre au premier plan** : application, titre de la fenêtre, et
  identité du document ou de l'e-mail quand elle peut être déduite (nom du fichier Word ou PDF,
  objet de l'e-mail ouvert, titre de l'onglet du navigateur pour Kleos). Il en fait des
  **segments** avec un début et une fin.
- **Inactivité** : au-delà de 5 minutes sans clavier ni souris (réglable), le segment se ferme.
  Au retour, si l'absence dépasse 15 minutes, la pastille propose discrètement « Absent 25 min,
  l'attribuer à un dossier ? » (cas d'un appel ou d'un rendez-vous non noté). Si on l'ignore,
  rien n'est compté.
- **Verrouillage de session et veille** : arrêt de l'enregistrement.
- **Pause** : plus aucun enregistrement jusqu'à la reprise.
- **Exclusions** : liste réglable d'applications et de mots dans les titres qui ne sont
  **jamais enregistrés** (ni titre, ni durée). Vide par défaut, avec des suggestions à l'installation.

### 6.3 Le rattachement aux dossiers

Pas d'IA à l'étape 1. Le rattachement repose sur des règles et sur l'apprentissage par correction :

- **Correspondances** : présence dans le titre, le nom de fichier ou l'objet de l'e-mail des
  mots-clés d'un dossier (nom du client, adversaire, référence Kleos, mots-clés libres), sans
  tenir compte de la casse ni des accents. Autres signaux : un fichier situé dans le dossier
  local du dossier ; un e-mail dont l'expéditeur ou la conversation est déjà rattaché à un dossier.
- **Ambiguïté** : si plusieurs dossiers correspondent, ou si aucun ne correspond, le segment est
  « non reconnu ». Il n'est jamais attribué au hasard.
- **Continuité** : un court segment non reconnu (moins de 2 minutes) entre deux segments du même
  dossier peut hériter de ce dossier. C'est marqué comme tel dans le récapitulatif.
- **Apprentissage** : quand l'utilisateur corrige une attribution, l'outil propose « Toujours
  associer [motif] à [dossier] ? ». La règle est enregistrée s'il accepte.
- **Hors dossier** : un temps peut être rattaché à « Cabinet / non facturable ». Il ne part pas
  vers Kleos mais reste visible.

### 6.4 Outlook (classique)

**Accès** : l'automatisation COM d'Outlook, en **lecture seule**. L'outil n'envoie, ne déplace,
ne supprime et ne modifie aucun élément.

**E-mails reçus** (boîte de réception) :

- À l'arrivée d'un e-mail, ou au rattrapage au démarrage sur les derniers jours, l'outil cherche
  le dossier (expéditeur connu, conversation déjà rattachée, mots-clés de l'objet).
- **S'il est rattaché** : l'e-mail (.msg) et ses pièces jointes sont enregistrés dans le dossier
  local, sous `Courrier/AAAA-MM-JJ – objet/`. Les signatures et images insérées dans le corps du
  message sont ignorées.
- **S'il n'est pas rattaché** et qu'il vient d'un expéditeur extérieur avec des pièces jointes :
  il apparaît dans **« À vérifier »**, avec trois choix : *Ranger dans…*, *Ignorer*, *Nouveau dossier*.
- **Nouveau dossier** : une fiche préremplie à partir de l'e-mail (nom de l'expéditeur, objet),
  que l'utilisateur complète. Un bouton *Copier pour Kleos* sert à créer le dossier dans Kleos.
- Le temps passé à lire et écrire des e-mails est compté par le suivi d'activité (§ 6.2), pas déduit des e-mails.

**Agenda** :

- Lecture des événements du jour et des 7 prochains jours, pour la section « À venir ».
- Les événements passés du jour pendant lesquels l'ordinateur n'a pas été utilisé (audience,
  rendez-vous extérieur) sont proposés comme lignes de temps dans le récapitulatif, avec leur
  durée et le dossier reconnu dans leur intitulé. Ces lignes sont à valider comme les autres.

**Ne pas gêner Outlook** : les appels se font hors de l'interface de la pastille, sont espacés
et tolèrent qu'Outlook soit fermé, en train de démarrer ou occupé.

**Garde de sécurité d'Outlook** *(choix de Claude)*. Lire l'adresse de l'expéditeur ou
enregistrer un .msg depuis un programme extérieur peut déclencher les alertes de sécurité
d'Outlook, selon l'état de l'antivirus et les règles du poste. Le premier livrable est donc un
**petit outil de diagnostic** à lancer sur le poste de l'utilisateur. Il vérifie l'accès à Outlook,
à la fenêtre active et au dossier de destination, et n'affiche que des résultats techniques
(« accès OK », « alerte déclenchée »), sans aucune donnée client. Si les alertes apparaissent,
la partie Outlook est déplacée dans un **complément chargé dans Outlook**, qu'Outlook considère
comme fiable, et l'application lui parle localement. Le plan doit prévoir cette bifurcation.

**Dossiers surveillés** *(choix de Claude)* : la boîte de réception, plus les sous-dossiers
Outlook choisis dans les réglages, pour le cas où des règles y rangent le courrier. Un
sous-dossier Outlook peut être associé à un dossier : c'est alors le signal de rattachement le
plus fiable.

### 6.5 Le récapitulatif de fin de journée

Ouvert par *Valider ma journée* ou par le rappel du soir. Esquisse :

```
Vendredi 20 novembre — à valider
────────────────────────────────────────────────────────────────
Dossier            Ce que vous avez fait             Durée
DUPONT / SCI Lys   Rédaction : Conclusions_v3.docx   1 h 40
MARTIN c/ Axa      3 e-mails, lecture de pièces      0 h 35
BERNARD            Audience TJ (agenda)              2 h 00
? non reconnu      Absence 14 h 10 – 14 h 30         0 h 20  → choisir
────────────────────────────────────────────────────────────────
[Tout valider]   [Copier pour Kleos]
```

- **Une ligne par dossier et par nature d'activité.** La description est générée automatiquement
  (« Rédaction : <fichier> », « N e-mails », « Lecture : <pièce> », « Audience (agenda) »), et
  le détail des segments s'ouvre en dépliant la ligne.
- **Modifications** : changer de dossier, la durée ou la description ; fusionner ; scinder ;
  supprimer ; ajouter une ligne manuelle.
- **Champs d'une ligne**, alignés sur la saisie dans Kleos : dossier, date, durée, **type de
  prestation**, description. Le type est proposé d'après l'activité (rédaction, correspondance,
  lecture et analyse, audience, rendez-vous, téléphone) et se corrige d'un clic. La liste des
  types est réglable pour reprendre exactement celle de Kleos.
- **Durées** en heures et minutes (Kleos accepte les minutes comme les heures). **Arrondi** à
  5 minutes, au plus proche, par défaut ; l'unité est réglable *(choix de Claude)*.
- **Validation** : les lignes deviennent des **temps validés**, conservés et modifiables ensuite.
- **Copier pour Kleos** : une ligne ou toutes, vers le presse-papiers, dans un format pratique à
  reporter (date, dossier, référence Kleos, durée, description). Si Kleos se remplit champ par
  champ, chaque champ d'une ligne a aussi son propre bouton de copie, et la ligne est cochée
  « reportée » une fois traitée *(choix de Claude)*.
- **Jours non validés** : ils restent en attente et sont signalés. On peut valider les jours précédents.
- **Historique** : consultation des jours passés, export CSV des temps validés.

### 6.6 La liste des dossiers

Une liste locale minimale, utilisée uniquement pour le rattachement et le classement :

- nom court affiché (« MARTIN c/ Axa »), client, parties adverses, référence Kleos, mots-clés,
  adresses e-mail associées (apprises ou saisies), dossier local sur le disque, statut (ouvert ou archivé) ;
- création manuelle, ou depuis un e-mail (§ 6.4) ;
- **création rapide** depuis l'état « Dossier ? » de la pastille et depuis une ligne non reconnue
  du récapitulatif (nom court et mots-clés suffisent), pour que la liste se remplisse au fil des
  premiers jours sans saisie préalable *(choix de Claude)* ;
- **mise en route** : un écran de saisie rapide pour entrer la trentaine de dossiers ouverts
  (nom court et mots-clés, le reste est facultatif), en une quinzaine de minutes ;
- import depuis un fichier CSV ou Excel, si Kleos permet d'exporter la liste des dossiers (à vérifier, facultatif) ;
- rattachement d'un dossier à un répertoire existant sur le disque, si l'utilisateur en a déjà un par affaire ;
- dossier racine réglable pour les dossiers locaux (par exemple `Documents\Dossiers\`).

## 7. Confidentialité et données

- **Aucune connexion réseau sortante** de la part de l'outil : pas de télémétrie, pas de mise à
  jour automatique, pas de rapport de plantage en ligne. C'est une exigence de conception, à
  vérifier par des tests.
- **Stockage local, chiffré**, dans le profil Windows de l'utilisateur. La clé est protégée par
  Windows, au niveau de la session de l'utilisateur.
- **Détail brut de l'activité** (segments avec titres) : effacé automatiquement après 30 jours.
  Les **temps validés** sont conservés.
- **Journaux techniques** : aucune donnée client (pas de titres, de noms de fichiers, d'objets ni d'adresses).
- **Sauvegarde** : copie chiffrée quotidienne des données vers un dossier choisi par
  l'utilisateur (par exemple un disque du cabinet), pour éviter la perte des temps validés.
  *(Choix de Claude.)* La sauvegarde est chiffrée avec une **clé de secours** générée à
  l'installation, que l'utilisateur imprime ou conserve en lieu sûr. Une clé liée uniquement à
  la session Windows rendrait la sauvegarde illisible après une panne de disque ou une
  réinstallation, c'est-à-dire précisément quand on en a besoin. La restauration sur un nouveau
  poste demande cette clé.
- **Dossiers synchronisés** *(choix de Claude)* : si le dossier racine des dossiers ou le dossier
  de sauvegarde est synchronisé avec un service en ligne (OneDrive, SharePoint, Dropbox…),
  l'outil le signale clairement à l'utilisateur avant de l'utiliser.
- **Signalement de problèmes** : un rapport technique exportable à la demande, sans donnée
  client, que l'utilisateur peut transmettre au développeur.
- **Désinstallation** : l'utilisateur choisit de conserver ou d'effacer ses données.

## 8. Approches envisagées

| Approche | Pour | Contre | Décision |
|---|---|---|---|
| **Application Windows native en C# / .NET** | La plus stable et la plus légère ; meilleure intégration à Windows (fenêtre active, inactivité, zone de notification) et à Outlook classique (COM) ; un seul installateur | Développée ici sous Linux : la partie Windows se teste sur une machine Windows (intégration continue) et chez l'utilisateur | **Retenue** |
| Application à interface web (type Tauri) | Interface facile à soigner ; Mac possible plus tard | Lien avec Outlook (COM) plus fragile ; l'avantage Mac compte peu, Kleos ne fonctionnant pas sur Mac | Écartée |
| Programme Python | Première version la plus rapide ; très bon écosystème pour l'IA de l'étape 2 | Installation moins propre, faux positifs fréquents des antivirus, impression moins « solide » | Écartée |

Choix proposé par Claude ; l'utilisateur ne l'a pas contesté. L'IA locale de l'étape 2
pourra tourner à côté (moteur local appelé depuis l'application) sans remettre ce choix en cause.

## 9. Décisions prises avec l'utilisateur

- Garder Kleos ; outil personnel d'abord ; extension aux collègues si le pilote est concluant.
- IA strictement locale, aucune donnée envoyée à une entreprise (pour l'étape 2).
- Ordre : étape 1 (temps) puis étape 2 (instruction).
- Suivi d'activité continu (option 1) plutôt qu'une estimation à partir des traces.
- Garde-fous proposés et acceptés : bouton pause, exclusions, détail brut chiffré et effacé après 30 jours.
- Widget : **la pastille** (option A), qui s'ouvre au clic.
- Outlook classique ; agenda lu dans Outlook.
- Report dans Kleos par copie (pas d'API) au départ.
- L'outil ne fait jamais rien de définitif sans validation, et n'envoie jamais rien.

## 10. Laissé au constructeur

- Le cadre d'interface (WPF ou WinUI 3) ; WPF sur **.NET 10** (version maintenue jusqu'en
  novembre 2028 ; .NET 8 n'est plus maintenu depuis le 10 novembre 2026) est préféré pour sa
  maturité et parce qu'il se compile depuis Linux (vérifié).
- La technique de suivi de la fenêtre active (interrogation périodique ou abonnement aux événements Windows).
- Le moteur de stockage et le mécanisme de chiffrement (dans le respect du § 7).
- Le format exact de l'installateur. Il doit pouvoir s'installer **sans droits administrateur**
  (installation pour l'utilisateur seul) et démarrer avec Windows. L'installateur n'étant pas signé
  pendant le pilote, Windows affichera un avertissement à l'installation.
- La stratégie de test : la logique métier (segmentation, rattachement, agrégation, arrondi,
  purge) dans une bibliothèque testée partout ; les parties Windows et Outlook derrière des
  interfaces, testées sur une machine Windows avec des données fictives. Le test réel avec Outlook
  se fait chez l'utilisateur.
- Les données de test : exclusivement fictives.
- **Livraison** *(choix de Claude)* : pas de mise à jour automatique (§ 7). Chaque version est
  un nouvel installateur que l'utilisateur télécharge et lance ; il conserve les données.
  Ordre de livraison : d'abord l'outil de diagnostic (§ 6.4), puis la pastille avec le suivi
  d'activité et le récapitulatif, puis Outlook.

## 11. Points ouverts

- **Export de la liste des dossiers depuis Kleos** : à vérifier par l'utilisateur (facultatif).
- **Lieu de conservation du code** et machine Windows de test : un dépôt privé avec une
  intégration continue Windows est nécessaire. Le choix se fait avec l'utilisateur avant le développement.
- **Tests réels avec Outlook** : uniquement sur le poste de l'utilisateur, via l'outil de
  diagnostic puis les versions successives. Le développeur n'a jamais accès au poste.

## 12. Évolution du 22 novembre 2026 : Windows, Mac, puis téléphone

Décisions de l'utilisateur, prises après la rédaction initiale :

- **Plateformes** : l'outil doit fonctionner sur le PC Windows du cabinet **et sur un Mac** (Outlook
  sur les deux), puis plus tard sur un téléphone **Android**. Priorité : les ordinateurs ; premier
  essai demandé sur Mac.
- **Partage entre appareils (option A retenue)** : les données sont chiffrées sur l'appareil, la
  clé ne quitte jamais les appareils de l'utilisateur, et un serveur relais transmet sans pouvoir
  lire. À construire après la première version.
- **Conséquence technique** *(choix de Claude)* : l'interface passe de WPF (Windows seul) à
  **Avalonia** (C#, Windows et macOS avec le même code, Android possible ensuite). La logique
  existante est conservée. Le diagnostic Windows reste valable pour le PC du cabinet.
- **Sur Mac**, la lecture du titre de la fenêtre active demande l'autorisation « Accessibilité »
  (Réglages Système → Confidentialité et sécurité). Sans elle, l'outil ne voit que le nom de
  l'application et les dossiers ne peuvent pas être reconnus automatiquement.
- **Première version (v1, ordinateurs)** : pastille, suivi d'activité, liste des dossiers,
  rattachement par mots-clés, récapitulatif du jour avec copie pour Kleos, stockage local chiffré,
  pause et exclusions. Outlook, sauvegarde avec clé de secours, synchronisation et téléphone
  viennent ensuite.
