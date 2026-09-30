# GameDiag

Observateur TCP local, en lecture seule, pour comprendre un protocole binaire. Le programme écoute sur la machine (`127.0.0.1`), relaie chaque octet vers le serveur réel, et journalise ce qu'il reconnaît. Il ne joue pas, n'injecte rien, et ne déchiffre pas TLS.

## Garde-fous

- **Lecture seule.** Chaque lecture est écrite telle quelle sur l'autre socket. Aucun octet n'est ajouté, retiré, réordonné ou réécrit. Le code ne contient pas de chemin d'injection.
- **Pas d'automatisation.** Pas de clic, pas de déplacement, pas de boucle de jeu. C'est un observateur.
- **Usage local.** L'écoute est refusée en dehors de `127.0.0.1` / `::1`. Le fichier hosts, s'il est modifié, ne l'est que sur cette machine, avec votre accord (`accept_terms: true` et `hosts.enabled: true`).
- **Conditions du jeu.** Un outil tiers peut être interdit par les conditions d'utilisation. GameDiag ne contourne pas d'anti-triche, ne lit pas la mémoire du client, et ne modifie pas le client. La décision de l'utiliser vous appartient.
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

Les signatures (taille, préfixe, contexte, champs) vivent dans `rules.yaml`. Le code ne contient pas de numéro de message. Une règle dont la signature n'est pas confirmée reste `enabled: false`. Si deux règles actives reconnaissent les mêmes octets, ou si un tableau de prix ne tombe pas juste dans la trame, rien n'est enregistré.

Les prix sont rangés par serveur. `Hell Mina` et `hellmina` désignent le même serveur. Un lot dont le total vaut 0 est ignoré, pour ne pas remplacer un prix déjà vu. Le prix unitaire n'est calculé que lorsque le total est divisible par la quantité.

Chaque connexion garde son propre état (personnage, position, combat). Un même prix vu par deux fenêtres n'est compté qu'une fois. Le titre de fenêtre, si `windows.enabled` est vrai, ne sert qu'à rapprocher un nom déjà lu dans le flux ; un rapprochement ambigu est laissé vide.

Au démarrage de session, un rappel peut retirer un bloc hosts oublié après un plantage :

```bash
dotnet run --project src/GameDiag -- --install-cleanup-task
```

## Lancer

.NET 8 SDK.

```bash
dotnet test
dotnet run --project src/GameDiag -- --self-test
```

L'auto-test vérifie le fichier hosts sur un fichier temporaire, puis un proxy vers un serveur local fictif : les octets des deux sens restent identiques, et SQLite contient les champs décodés.

Pour observer un vrai flux, éditez `src/GameDiag/config.yaml` :

1. `accept_terms: true`
2. `upstream_host` : une **adresse IP**, jamais un nom DNS (un nom résolu par le fichier hosts rebouclerait vers le proxy)
3. les ports d'écoute. `5555` est le port applicatif. `443` est un passe-plat : s'il ne peut pas être lié, il est ignoré et `5555` continue. Rien n'est déchiffré.

```bash
dotnet run --project src/GameDiag -- --config src/GameDiag/config.yaml
```

Ctrl+C arrête le relay et retire le bloc hosts s'il a été installé. Si l'adresse upstream ne répond pas, la session se ferme au bout de 10 secondes, sans qu'aucun octet ne soit écrit au client.

Si le protocole passe par TLS, les règles ne voient que des enregistrements opaques. GameDiag ne termine pas TLS.

## Règles YAML

`rules.yaml` est relu automatiquement. Une erreur de syntaxe laisse l'ensemble précédent en place. La première règle dont le préfixe correspond, dont la longueur est valide et dont le contexte est satisfait est retenue. Si le préfixe correspond mais que le contexte échoue, la trame est consommée sans être enregistrée, afin de rester aligné. Sinon le décodeur avance d'un octet.

Les règles livrées décrivent un protocole **fictif** (`HI` / `GD`). Remplacez-les par vos propres observations. Champs : `uint8`, `uint16`, `int16`, `uint32`, `int32`, `hex`.

`length_field.bias` s'ajoute à la valeur lue pour obtenir la taille totale. `bias: 0` quand la longueur inclut déjà l'en-tête. Sans `length_field`, la trame a une taille fixe (`min_length` = `max_length`).

Le contexte (`requires`, `forbids`, `sets`) est partagé par les deux sens d'une même connexion. Les tampons d'octets, eux, restent séparés : TCP n'aligne pas les messages sur les segments.

## SQLite

Les insertions sont groupées (`flush_interval_ms`, `flush_batch_size`) sur un thread qui n'est pas celui du relay. Une file pleine abandonne l'observation au lieu de ralentir le décodeur, qui lui-même n'attend pas les sockets.

```bash
sqlite3 gamediag.db "select observed_at, direction, rule_id, fields_json from observations order by id desc limit 20;"
```

## Fichier hosts

Désactivé par défaut (`hosts.enabled: false`). Quand il est activé, GameDiag écrit un bloc borné par `# GAMEDIAG-BEGIN` et `# GAMEDIAG-END`, sans toucher aux autres lignes. Un second lancement ne duplique pas le bloc.

Le retrait a lieu sur Ctrl+C, SIGTERM et à la sortie du processus. Un `SIGKILL` ne peut pas exécuter ce retrait : au démarrage suivant, le bloc marqué est retiré ou remplacé, y compris s'il n'a pas de marqueur de fin. On peut aussi le retirer sans relancer le proxy :

```bash
dotnet run --project src/GameDiag -- --remove-hosts
dotnet run --project src/GameDiag -- --remove-hosts --hosts-path /etc/hosts
```

Modifier le fichier hosts du système demande les droits administrateur. Les noms d'hôte sont limités aux lettres, chiffres, `.` et `-`, pour qu'une valeur ne puisse pas ajouter une autre ligne.
