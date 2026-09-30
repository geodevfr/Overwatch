# Overwatch

Observateur TCP local, en lecture seule, pour comprendre un protocole binaire. Le programme écoute sur la machine (`127.0.0.1`), relaie chaque octet vers le serveur réel, et journalise ce qu'il reconnaît. Il ne joue pas, n'injecte rien, et ne déchiffre pas TLS.

## Garde-fous

- **Lecture seule.** Chaque lecture est écrite telle quelle sur l'autre socket. Aucun octet n'est ajouté, retiré, réordonné ou réécrit. Le code ne contient pas de chemin d'injection.
- **Pas d'automatisation.** Pas de clic, pas de déplacement, pas de boucle de jeu. C'est un observateur.
- **Usage local.** L'écoute est refusée en dehors de `127.0.0.1` / `::1`. Le fichier hosts, s'il est modifié, ne l'est que sur cette machine, avec votre accord (`accept_terms: true` et `hosts.enabled: true`).
- **Conditions du jeu.** Un outil tiers peut être interdit par les conditions d'utilisation, même s'il est passif. Une sanction reste possible. Overwatch ne lit pas la mémoire du client, ne le modifie pas, n'injecte rien, et n'essaiera pas d'échapper à un contrôle ajouté plus tard. L'usage reste le vôtre.
- **Pas de diffusion.** SQLite reste un fichier local. Rien n'est envoyé à un serveur tiers.
- **TLS opaque.** Un listener sur le port 443, si vous l'activez, recopie les octets. Il n'y a pas de certificat, pas de `SslStream`, pas de déchiffrement.

Si une évolution sort de ce cadre (réécriture de paquets, action automatique, contournement), elle n'a pas sa place ici.

## Chemin des octets

```
client  --lecture-->  relay  --écriture immédiate-->  upstream
                         |
                         +-- copie, file bornée --> décodeur --> file bornée --> SQLite
```

Le relay active `TCP_NODELAY` des deux côtés. Il écrit le tampon reçu avant d'en copier un exemplaire pour l'analyse. Cette copie est déposée dans une file à capacité fixe. Si le décodeur est en retard, l'échantillon le plus ancien est abandonné : `Publish` ne s'endort jamais en attendant le décodeur. Le seul blocage possible du relay est le tampon d'envoi TCP du pair, c'est-à-dire le contrôle de flux normal.

Une perte d'échantillon crée un trou de séquence. Le décodeur jette alors le morceau de trame en cours et oublie les drapeaux de conversation de cette connexion, pour ne pas recoller des octets qui ne se suivent plus.

Le décodeur travaille par tranches d'une milliseconde, puis rend la main. Le watchdog signale un traitement au-delà de 300 ms avec l'identifiant de la règle, ou à défaut le préfixe hexadécimal du flux. Ce signal ne ralentit pas les sockets.

Les ports `5555` et `443` sont tous les deux prévus : si un réseau bloque l'un, le client bascule sur l'autre. Le port `443` reste un passe-plat. S'il s'agit de TLS, les signatures ne correspondent pas et aucune valeur n'est inventée.

## Prix et clients

Le client visé est Dofus 3. Les versions de protocole `3.6.8.8`, `3.6.9.9` et `3.6.11.13` sont listées dans la table de détection de `rules.yaml` avec le statut `unknown`. Le protocole est régénéré à chaque mise à jour (codes de messages et numéros de champs). Le code n'en contient aucun : une signature n'est active que si elle est écrite dans cette table et que sa version est passée à `captured` après une capture. Une signature déposée sous une version encore `unknown` est ignorée.

Si deux règles actives reconnaissent les mêmes octets, ou si un tableau de prix ne tombe pas juste dans la trame, rien n'est enregistré.

Les prix sont rangés par serveur. `Hell Mina` et `hellmina` désignent le même serveur. Un lot dont le total vaut 0 est ignoré, pour ne pas remplacer un prix déjà vu. Le prix unitaire n'est calculé que lorsque le total est divisible par la quantité.

Chaque connexion garde son propre état (personnage, position, combat). Un même prix vu par deux fenêtres n'est compté qu'une fois. Le titre de fenêtre, si `windows.enabled` est vrai, ne sert qu'à rapprocher un nom déjà lu dans le flux ; un rapprochement ambigu est laissé vide.

Au démarrage de session, un rappel peut retirer un bloc hosts oublié après un plantage :

```bash
dotnet run --project src/Overwatch -- --install-cleanup-task
```

## Lancer

.NET 8 SDK.

Double-cliquez `Lancer Overwatch.bat` (Windows). Le navigateur s'ouvre sur une page de cet ordinateur, `http://127.0.0.1:47321`. Rien à taper : la confirmation, l'adresse du serveur, le démarrage et la capture sont sur cette page.

```bash
dotnet test
dotnet run --project src/Overwatch
dotnet run --project src/Overwatch -- --self-test
```

`--relay` garde l'ancien mode sans page, pour un lancement déjà configuré.

L'auto-test vérifie le fichier hosts sur un fichier temporaire, puis un proxy vers un serveur local fictif : les octets des deux sens restent identiques, et SQLite contient les champs décodés.

Pour observer un vrai flux, éditez `src/Overwatch/config.yaml` :

1. `accept_terms: true`
2. `upstream_host` : une **adresse IP**, jamais un nom DNS (un nom résolu par le fichier hosts rebouclerait vers le proxy)
3. les ports d'écoute. `5555` est le port applicatif. `443` est un passe-plat : s'il ne peut pas être lié, il est ignoré et `5555` continue. Rien n'est déchiffré.

```bash
dotnet run --project src/Overwatch -- --config src/Overwatch/config.yaml
```

Ctrl+C arrête le relay et retire le bloc hosts s'il a été installé. Si l'adresse upstream ne répond pas, la session se ferme au bout de 10 secondes, sans qu'aucun octet ne soit écrit au client.

Si le protocole passe par TLS, les règles ne voient que des enregistrements opaques. Overwatch ne termine pas TLS.

## Table de détection

`rules.yaml` est la table de détection. Il est relu automatiquement. Une erreur de syntaxe laisse l'ensemble précédent en place.

Le fichier livré nomme le client `dofus3` et trois versions, toutes `unknown`, sans préfixe ni offset. `pending` indique seulement quelles observations une capture devra renseigner (`average_prices`, `sale_lots`, `server_name`, `character_name`, `position`, `combat`). Tant que `status` vaut `unknown`, aucune règle de cette version n'est compilée.

Après une capture de **cette** version, les signatures vont dans `detection[].rules` (préfixe, longueur, champs). Puis `status: captured`. Une autre version reste inactive : ses codes et ses numéros de champs ne sont pas ceux de la précédente. Le bloc `rules:` à la racine est refusé dès que `client` est renseigné. Il ne sert qu'au protocole fictif de l'auto-test, qui n'est pas chargé comme client.

La première règle dont le préfixe correspond, dont la longueur est valide et dont le contexte est satisfait est retenue. Si le préfixe correspond mais que le contexte échoue, la trame est consommée sans être enregistrée, afin de rester aligné. Sinon le décodeur avance d'un octet.

Champs : `uint8`, `uint16`, `int16`, `uint32`, `int32`, `hex`, `utf8`.

`length_field.bias` s'ajoute à la valeur lue pour obtenir la taille totale. `bias: 0` quand la longueur inclut déjà l'en-tête. Sans `length_field`, la trame a une taille fixe (`min_length` = `max_length`).

Le contexte (`requires`, `forbids`, `sets`) est partagé par les deux sens d'une même connexion. Les tampons d'octets, eux, restent séparés : TCP n'aligne pas les messages sur les segments.

## SQLite

Les insertions sont groupées (`flush_interval_ms`, `flush_batch_size`) sur un thread qui n'est pas celui du relay. Une file pleine abandonne l'observation au lieu de ralentir le décodeur, qui lui-même n'attend pas les sockets.

```bash
sqlite3 overwatch.db "select observed_at, direction, rule_id, fields_json from observations order by id desc limit 20;"
```

## Fichier hosts

Désactivé par défaut (`hosts.enabled: false`). Quand il est activé, Overwatch écrit un bloc borné par `# >>> overwatch >>>` et `# <<< overwatch <<<`, sans toucher aux autres lignes. Un second lancement ne duplique pas le bloc. Un ancien bloc `# OVERWATCH-BEGIN` est repris puis remplacé.

L'attribut lecture seule est retiré le temps de l'écriture, puis remis. Les ACL du fichier ne sont pas modifiées. Chaque résultat (succès ou échec, y compris une écriture annulée par l'antivirus ou l'accès contrôlé aux dossiers) est ajouté à `%LOCALAPPDATA%\Overwatch\hosts.log`.

Sans droit d'écriture, le processus ne s'arrête pas : le relais démarre, le fichier hosts reste tel quel, et l'écran affiche que le jeu ne passera pas par Overwatch. Aucune capture du jeu n'est alors possible.

Le retrait a lieu sur Ctrl+C, SIGTERM et à la sortie du processus. Un arrêt brutal laisse le bloc : à l'ouverture suivante de l'écran, il est retiré avant que le relais n'écoute, même sans marqueur de fin. La tâche `OverwatchHostsCleanup`, créée avec `--install-cleanup-task` ou le bouton de l'écran, fait ce retrait à l'ouverture de session. Elle ne réécrit pas la redirection : au démarrage de Windows, personne n'écoute encore, et le jeu ne doit pas être renvoyé vers cet ordinateur. On peut aussi retirer le bloc à la main :

```bash
dotnet run --project src/Overwatch -- --remove-hosts
dotnet run --project src/Overwatch -- --remove-hosts --hosts-path /etc/hosts
```

Modifier le fichier hosts du système demande les droits administrateur. Les noms d'hôte sont limités aux lettres, chiffres, `.` et `-`, pour qu'une valeur ne puisse pas ajouter une autre ligne.
