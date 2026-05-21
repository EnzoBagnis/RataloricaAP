French 
---

Installation côté jeu

  1. Trouve le dossier d'installation du jeu : clic droit sur Ratalorica dans Steam → Gérer → Parcourir les fichiers
  locaux. C'est typiquement …\steamapps\common\Ratalorica\.
  2. Débloque l'archive BepInEx avant extraction : clic droit sur le .zip téléchargé → Propriétés → coche "Débloquer" →
  OK. (Sinon Windows marque les DLLs winhttp.dll etc. comme Mark-of-the-Web et refuse de les charger, BepInEx ne
  s'injecte pas.)
  3. Extrais le contenu du .zip directement dans le dossier du jeu (à côté de Ratalorica.exe).
  4. Crée steam_appid.txt dans le dossier du jeu avec pour seul contenu :
  4592560
  5. Lance le jeu une première fois pour que BepInEx génère ses dossiers (BepInEx/config/, BepInEx/plugins/, etc.).
  Ferme le jeu.
  6. Copie RataloricaAP.dll dans <game>\BepInEx\plugins\.
  7. Crée un raccourci direct vers Ratalorica.exe sur le bureau (clic droit → Envoyer vers → Bureau). C'est ce raccourci
   qu'il faut utiliser pour lancer le jeu — pas le bouton Steam qui empêche BepInEx de s'injecter. Garde Steam ouvert en
   arrière-plan pour que le SDK Steamworks (achievements, cloud) continue de fonctionner.

  Installation côté Archipelago

  8. Installe le world : copie ratalorica.apworld dans le dossier worlds/ (ou custom_worlds/ selon la version) de
  l'install Archipelago, ou upload-le sur archipelago.gg avant de générer ta seed.
  9. Génère ton YAML (template dans Ratalorica.yaml) et utilise-le pour la génération de la seed.

  Utilisation

  10. Lance Ratalorica via le raccourci direct (Steam en arrière-plan).
  11. F1 pour toggler la fenêtre Archipelago.
  12. Remplis Server / Slot Name / Password → Connect.
  13. Pour switcher de run sans relancer le jeu : Disconnect dans la GUI → change Server/Slot → Connect.

# English

---

  Game-side installation

1. Locate the game's installation folder: Right-click on Ratalorica in Steam → Manage → Browse Local Files.
It's typically located at …\steamapps\common\Ratalorica\.
2. Unblock the BepInEx archive before extraction: Right-click on the downloaded .zip file → Properties → check "Unblock" →
OK. (Otherwise, Windows will mark DLLs like winhttp.dll, etc., as Mark-of-the-Web and refuse to load them; BepInEx will not be injected.)
3. Extract the contents of the .zip file directly into the game's folder (next to Ratalorica.exe).
4. Create steam_appid.txt in the game folder with only the following content:
4592560
5. Launch the game once so that BepInEx generates its folders (BepInEx/config/, BepInEx/plugins/, etc.).
Close the game.
6. Copy RataloricaAP.dll to <game>\BepInEx\plugins\.
7. Create a direct shortcut to Ratalorica.exe on the desktop (right-click → Send to → Desktop). This is the shortcut
you must use to launch the game — not the Steam button, which prevents BepInEx from injecting itself. Keep Steam open in the
background so that the Steamworks SDK (achievements, cloud) continues to function.

Archipelago Installation

8. Install the world: copy ratalorica.apworld to the worlds/ folder (or custom_worlds/ depending on the version) of your Archipelago installation, or upload it to archipelago.gg before generating your seed.
9. Generate your YAML (template in Ratalorica.yaml) and use it to generate the seed.

Usage

10. Launch Ratalorica using the shortcut (Steam in the background).
11. Press F1 to toggle the Archipelago window.
12. Fill in Server / Slot Name / Password → Connect.
13. To switch runs without restarting the game: Disconnect in the GUI → change Server/Slot → Connect.
