Installation côté jeu

  1. Trouve le dossier d'installation du jeu : clic droit sur Ratalorica dans Steam → Gérer → Parcourir les fichiers
  locaux. C'est typiquement …\steamapps\common\Ratalorica\.
  2. Débloque l'archive BepInEx avant extraction : clic droit sur le .zip téléchargé → Propriétés → coche "Débloquer" →
  OK. (Sinon Windows marque les DLLs winhttp.dll etc. comme Mark-of-the-Web et refuse de les charger, BepInEx ne
  s'injecte pas.)
  3. Extrais le contenu du .zip directement dans le dossier du jeu (à côté de Ratalorica.exe).
  4. Crée steam_appid.txt dans le dossier du jeu avec pour seul contenu :
  4592560
  4. (Empêche le Steam DRM de relancer le jeu via Steam et de bypasser BepInEx.)
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
