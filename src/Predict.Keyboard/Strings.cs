namespace Predict.Keyboard;

/// <summary>All user-visible strings of the app in one place (D14: French only for now; an English
/// resource file later replaces this class's values, not its call sites).</summary>
internal static class Strings
{
    public const string AppName = "Pépite";
    /// <summary>The tagline after the name (À propos, welcome window): « Pépite — prédiction de mots ».</summary>
    public const string AppTagline = "prédiction de mots";

    /// <summary>The project site: the only contact shown by the app and its documents (D18, owner 2026-11-14 — no
    /// personal name or e-mail). One constant: change it here when the site exists. (A code-signing certificate will
    /// still show its holder's name as the publisher in Windows dialogs.)</summary>
    public const string SiteUrl = "https://github.com/pepite-labs/pepite";
    /// <summary>Copyright line (D18): the project's authors collectively, no personal name.</summary>
    public const string Copyright = "© 2026 Les auteurs de Pépite";

    // tray
    public const string TrayActive = "Pépite — F1–F8 pour insérer";
    public const string TrayPaused = "Pépite — en pause";
    public const string MenuPause = "Pause";
    public const string MenuResume = "Reprendre";
    public const string MenuSettings = "Réglages…";
    /// <summary>The embedded help (plan « aide intégrée »), right before « À propos… ».</summary>
    public const string MenuHelp = "Aide";
    public const string MenuAbout = "À propos…";
    public const string MenuQuit = "Quitter";

    // À propos
    public const string AboutTitle = "À propos de Pépite";
    public const string AboutTagline = "Des mots proposés pendant que vous écrivez, dans toutes les applications Windows. Aucune connexion réseau.";
    public const string AboutVersion = "Version {0}";
    public const string AboutPictosHeader = "Pictogrammes";
    public const string AboutPictosResized = "Pictogrammes redimensionnés ; pack généré le {0}.";
    public const string AboutPictosOffMissing = "Images : désactivées (pack absent).";
    public const string AboutPictosOffOption = "Images : désactivées (option --no-pictures).";
    /// <summary>Publication cleanup (S6, legal item 3): the pictures are for non-commercial use only — said where they are credited.</summary>
    public const string AboutLicence = "Licence CC BY-NC-SA 4.0 — usage non commercial uniquement";
    public const string AboutLexiqueHeader = "Lexique";
    public const string AboutLexique = "Lexique 3.83 — New, B., Pallier, C. et al., www.lexique.org, licence CC BY-SA 4.0. " +
                                       "Liste de référence de l'indicateur d'orthographe : Grammalecte, « Lexique simplifié » v7.7 " +
                                       "(grammalecte.net), licence MPL 2.0. " +
                                       // plan 11 (10D-1): the agreement of the proposed words reads the morphology of grammar.sqlite
                                       "Accords des mots proposés : Grammalecte — lexique (MPL-2.0). " +
                                       // plan offensive-words (0.6): the « mots exclus » list (exclusions\fr.txt)
                                       "Mots exclus : choisis d'après les étiquettes d'usage du Wiktionnaire (contributeurs, " +
                                       "fr.wiktionary.org, extraction kaikki.org, licence CC BY-SA 4.0) et LDNOOBW (Shutterstock et " +
                                       "contributeurs, CC BY 4.0), " +
                                       "puis relus.";
    /// <summary>The corpora the word and context frequencies of prediction.sqlite are counted from (plan corpus-light,
    /// Phase 2: written sources only, no film subtitles; the full list with versions and SHA-256 is prediction-SOURCES.txt,
    /// written from Data.Build/data.lock.json). Leipzig states no CC BY version: credited as « CC BY » with its download
    /// page (owner 2026-11-15).</summary>
    public const string AboutCorpora = "Fréquences des mots et des suites de mots : phrases de Tatoeba (tatoeba.org), licence CC BY 2.0 FR ; " +
                                       "Leipzig Corpora Collection (wortschatz.uni-leipzig.de), corpus d'actualités français 2023, licence CC BY ; " +
                                       "Vikidia, l'encyclopédie des 8-13 ans (fr.vikidia.org), licence CC BY-SA 3.0 ; " +
                                       "livres pour enfants du domaine public (Project Gutenberg, gutenberg.org). " +
                                       "La base de mots est sous licence CC BY-SA 4.0 ; liste des sources : prediction-SOURCES.txt.";
    public const string AboutClose = "Fermer";
    public const string AboutFontsHeader = "Polices";
    /// <summary>{0} = family, {1} = copyright line of its OFL.txt.</summary>
    public const string AboutFontLine = "{0} — {1} SIL Open Font License 1.1.";
    public const string AboutFontMissing = "{0} — absente.";
    public const string AboutOflLink = "openfontlicense.org";
    // plan 08a: the Piper voice, espeak-ng, ONNX Runtime, NAudio (D19)
    public const string AboutVoicesHeader = "Voix";
    public const string AboutLicencesHeader = "Licences";
    public const string AboutVoiceMissing = "Voix naturelle : non installée (voix Windows seulement).";
    public const string AboutEngines =
        "Synthèse : Piper (piper1-gpl 1.8.0, GPL-3.0-or-later) ; phonèmes : espeak-ng 1.52.0.1 (GPL-3.0-or-later) ; " +
        "ONNX Runtime 1.31.1 (MIT) ; NAudio 2.2.1 (MIT).";
    public const string AboutNoticesLink = "Licences des composants (THIRD-PARTY-NOTICES.txt)";
    public const string AboutAppLicence = "Pépite est un logiciel libre sous licence GPL-3.0-or-later (COPYING).";
    /// <summary>« Site : » then the <see cref="SiteUrl"/> link (the site exists since the first public push, 0.10.1).</summary>
    public const string AboutSite = "Site\u00A0: ";
    public const string OflUrl = "https://openfontlicense.org";

    /// <summary>Fallback when the pack is absent or has no attribution in its meta table: verbatim from
    /// arasaac.org/terms-of-use (fr, retrieved 2026-11-08; see Data.Build/arasaac_attribution.txt).</summary>
    public const string ArasaacAttribution =
        "Les symboles pictographiques utilisés sont la propriété du Gouvernement d'Aragon et ont été créés par " +
        "Sergio Palao pour ARASAAC (http://www.arasaac.org), qui les distribuent sous Licence Creative Commons BY-NC-SA.";

    public const string ArasaacUrl = "https://arasaac.org";
    public const string LicenceUrl = "https://creativecommons.org/licenses/by-nc-sa/4.0/";
    public const string LexiqueUrl = "http://www.lexique.org";
    public const string ArasaacLinkText = "arasaac.org";
    public const string LexiqueLinkText = "lexique.org";

    // bar tooltips
    public const string TipPrefix = "Suite du mot";
    public const string TipPhonetic = "Même son (orthographe phonétique)";
    public const string TipEdit = "Lettres inversées / faute de frappe";
    public const string TipActions = " — clic : insérer, clic droit : écouter";
    // 2026-11-25 owner: listen before inserting (« Clic sur un mot », « Touches F1–F8 », A Maj + F1…F8, D the speaker band)
    public const string TipActionsListenFirst = " — 1er clic : écouter, 2e clic : insérer, clic droit : écouter";
    public const string TipActionsListenDouble = " — clic : écouter, double-clic ou clic droit : insérer";
    /// <summary>The click part of a tile's tooltip. With the speaker band (D) a click on the word inserts at once whatever
    /// the click mode (owner answer 4).</summary>
    public static string TipActionsFor(ClickMode mode, bool band) => (band ? ClickMode.Insert : mode) switch
    {
        ClickMode.ListenFirst => TipActionsListenFirst,
        ClickMode.ListenDouble => TipActionsListenDouble,
        _ => TipActions,
    };
    /// <summary>The Invoke part of a tile's UI Automation help text (owner 2026-11-25: an Invoke follows « Touches F1–F8 »,
    /// even with the F-keys off). Provisional wording, the owner may change it.</summary>
    public const string TileHelpInvokeListenFirst = "activer : écouter, puis encore une fois : insérer";
    public const string TileHelpInvokeInsert = "activer : insérer";
    /// <summary>The tile's UI Automation help text: the F-key part (<paramref name="fkey"/> = 3 for F3; null when the F-keys
    /// are off), then the click part (<see cref="TipActionsFor"/>), then the Invoke part (<see cref="TileHelpInvokeListenFirst"/>
    /// / <see cref="TileHelpInvokeInsert"/>, following <paramref name="keysListenFirst"/>).</summary>
    public static string TileHelp(int? fkey, bool keysListenFirst, ClickMode mode, bool band)
    {
        string click = (band ? ClickMode.Insert : mode) switch
        {
            ClickMode.ListenFirst => "clic : écouter, puis encore une fois : insérer ; clic droit : écouter",
            ClickMode.ListenDouble => "clic : écouter ; double-clic ou clic droit : insérer",
            _ => "clic : insérer ; clic droit : écouter",
        };
        string invoke = " ; " + (keysListenFirst ? TileHelpInvokeListenFirst : TileHelpInvokeInsert);
        if (fkey is not int n) return char.ToUpperInvariant(click[0]) + click.Substring(1) + invoke;
        return (keysListenFirst ? $"F{n} : écouter, puis encore une fois : insérer" : $"F{n} : insérer") + " ; " + click + invoke;
    }
    /// <summary>UI Automation name of option D's speaker band (« Écouter maison »).</summary>
    public const string ListenButtonName = "Écouter {0}";
    public const string ListenButtonTip = "Écouter ce mot (sans l'insérer)";
    /// <summary>UI Automation item status of the tile whose word was heard and waits for its second press.</summary>
    public const string ListenArmedStatus = "Écouté — encore une fois pour insérer";
    // Réglages (owner addendum 2026-11-25): « Clic sur un mot » (3 values) and « Touches F1–F8 » (2 values); no access key
    // for the lists and D (every letter of their labels is taken on this tab), « j » for A. Mode 3 is described as the usual
    // convention of other word-prediction tools — never by a product's name.
    public const string ClickModeLabel = "Clic sur un mot :";
    public const string ClickInsert = "Insérer tout de suite (clic droit : écouter)";
    public const string ClickListenFirst = "1er clic : écouter, 2e clic : insérer";
    public const string ClickListenDouble = "Clic : écouter ; double-clic ou clic droit : insérer";
    public const string ClickModeHint =
        "« Clic : écouter ; double-clic : insérer » est la convention habituelle d'autres outils de prédiction de mots " +
        "(un clic écoute, un double-clic insère). Un toucher compte comme un clic.";
    public const string ClickBandNote = "Avec le haut-parleur sous chaque mot, un clic sur le mot l'insère tout de suite.";
    public const string KeysModeLabel = "Touches F1–F8 :";
    public const string KeysInsert = "Insérer tout de suite";
    public const string KeysListenFirst = "1er appui : écouter, 2e : insérer";
    public const string KeysModeHint = "1er appui sur F1…F8 : le mot est lu. 2e appui sur la même touche : il est inséré.";
    public const string ListenShiftLabel = "Ma_j + F1…F8 : écouter le mot";
    public const string ListenShiftHint = "Quand la barre est affichée, Maj+F3, Maj+F5, Maj+F7… ne vont plus à l'application (par exemple Word).";
    public const string ListenButtonLabel = "Bouton haut-parleur sous chaque mot";
    public const string ListenButtonHint = "Un clic sur le haut-parleur, sous le mot, le lit. Un clic sur le mot lui-même l'insère alors tout de suite.";

    // bar grip (free position)
    public const string GripName = "Poignée de la barre";
    public const string GripHint = "Faites glisser la barre par sa poignée";

    // settings window (D8)
    public const string SettingsTitle = "Réglages — Pépite";
    public const string SecWords = "Mots";
    public const string SecPosition = "Position";
    public const string SecPreview = "Aperçu";
    public const string SecSpeech = "Voix";
    public const string WordCountLabel = "_Nombre de mots proposés";
    public const string WordCountRange = "(1–8)";
    public const string SizeLabel = "Taille du texte";
    public const string SizeS = "_Petit";
    public const string SizeM = "_Moyen";
    public const string SizeL = "_Grand";
    public const string SizeXL = "Très gran_d";
    public const string FontLabel = "P_olice";
    public const string FontSegoe = "Segoe UI";
    public const string FontOpenDyslexic = "OpenDyslexic";
    public const string FontLexend = "Lexend";
    public const string FontAndika = "Andika";
    public const string FontMissing = "{0} (police absente)";
    public const string SpacingLabel = "Espacement des lettres";
    public const string SpacingNormal = "No_rmal";
    public const string SpacingWider = "Plus _large";
    public const string SpacingWidest = "_Très large";
    public const string ShowPictures = "Afficher les im_ages";
    public const string PicturesMissing = "(pack d'images absent)";
    public const string KeysOn = "Touches _F1 à F8 pour insérer";
    /// <summary>D22: space before ; : ! ? typed after an inserted word (Alt+ç: a key of its own on AZERTY).</summary>
    public const string PunctLabel = "Espace avant ; : ! ?";
    public const string PunctFrench = "Fran_çaise (espace fine)";
    public const string PunctNone = "Aucune";
    public const string PosCaret = "À côté du _curseur";
    public const string PosTop = "En ha_ut de l'écran";
    public const string PosBottom = "En _bas de l'écran";
    public const string PosFree = "Po_sition libre (poignée)";
    public const string PosFreeHint = "Faites glisser la barre par sa poignée.";
    public const string SpeakInserted = "Lire le mot _inséré";
    public const string SpeakWords = "Lire cha_que mot après une espace";
    public const string SpeakSentences = "Lire la p_hrase à . ! ?";
    /// <summary>D23: re-read an edited word / sentence when the caret leaves it.</summary>
    public const string RereadEdits = "Relire le mot / la phrase modifi_é(e)";
    public const string VoiceLabel = "_Voix";
    public const string VoiceLoading = "Chargement…";
    public const string VoiceNone = "(aucune voix installée)";
    public const string VoiceMissing = "{0} (non installée)";
    /// <summary>Plan 08a: the Piper voice in « Réglages » (D24: the default when installed).</summary>
    public const string VoicePiperSiwis = "Siwis — voix naturelle (femme)";
    /// <summary>Plan 08b: the male Piper voice (pierre of fr_FR-upmc-medium; public since D28 changed on 2026-11-16).</summary>
    public const string VoicePiperPierre = "Pierre — voix naturelle (homme)";
    /// <summary>Another Piper voice pack: its id.</summary>
    public const string VoicePiperOther = "{0} — voix naturelle";
    // discreet voice-loading feedback (owner, 2026-11-14): a status line in Réglages and the tray tooltip, no popup
    public const string VoiceLoadingStatus = "Chargement de la voix {0}\u2026";
    public const string VoiceReadyStatus = "\u2713 Voix {0} prête";
    public const string VoiceFailedStatus = "Voix {0} indisponible — la voix Windows est utilisée";
    public const string TrayLoading = "Pépite — chargement de la voix {0}\u2026";
    /// <summary>« Pierre », « Siwis »: a Piper voice's short name (its id with a capital).</summary>
    public static string PiperShortName(string id) => id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id.Substring(1);
    /// <summary>The Réglages status line for a voice (<c>piper:&lt;id&gt;</c>) in a load state; null = nothing to show (a
    /// SAPI voice, or a Piper voice not loaded).</summary>
    public static string? VoiceStatus(string? voiceId, PiperState state)
    {
        if (voiceId is null || !voiceId.StartsWith("piper:", System.StringComparison.Ordinal)) return null;
        var name = PiperShortName(voiceId.Substring(6));
        return state switch
        {
            PiperState.Loading => string.Format(VoiceLoadingStatus, name),
            PiperState.Ready => string.Format(VoiceReadyStatus, name),
            PiperState.Failed => string.Format(VoiceFailedStatus, name),
            _ => null,
        };
    }
    /// <summary>The tray tooltip (NotifyIcon text, at most 127 characters): paused, loading the configured voice, or
    /// the normal one.</summary>
    public static string TrayText(bool paused, string? loadingVoiceId, string? profileName = null)
    {
        bool loading = loadingVoiceId is { } id && id.StartsWith("piper:", System.StringComparison.Ordinal);
        string voice = loading ? PiperShortName(loadingVoiceId!.Substring(6)) : "";
        var t = profileName is null
            ? (paused ? TrayPaused : loading ? string.Format(TrayLoading, voice) : TrayActive)
            : (paused ? string.Format(TrayPausedFor, profileName) : loading ? string.Format(TrayLoadingFor, profileName, voice)
                                                                            : string.Format(TrayActiveFor, profileName));
        return t.Length <= 127 ? t : t.Substring(0, 127);
    }
    /// <summary>The name of a Piper voice id in the voice list.</summary>
    public static string PiperVoiceName(string id) => id switch
    {
        "siwis" => VoicePiperSiwis,
        "pierre" => VoicePiperPierre,
        _ => string.Format(VoicePiperOther, id),
    };
    public const string RateLabel = "Vitesse";
    public const string RateSlow = "lent";
    public const string RateFast = "rapide";
    public const string VolumeLabel = "Volume";
    // the chosen speed / volume, shown next to the sliders and given to UI Automation (owner, 2026-11-14)
    public const string RateNormal = "Normale";
    public const string RateBitSlow = "Un peu lente";
    public const string RateSlower = "Lente";
    public const string RateVerySlow = "Très lente";
    public const string RateBitFast = "Un peu rapide";
    public const string RateFaster = "Rapide";
    public const string RateVeryFast = "Très rapide";
    /// <summary>« Normale (0) », « Un peu lente (−2) », « Rapide (+5) »: bands of the −10..10 slider (0; 1–3; 4–7;
    /// 8–10 each way), the number with a real minus sign.</summary>
    public static string RateValue(int rate)
    {
        rate = System.Math.Clamp(rate, -10, 10);
        int a = System.Math.Abs(rate);
        var word = rate == 0 ? RateNormal
            : rate < 0 ? (a <= 3 ? RateBitSlow : a <= 7 ? RateSlower : RateVerySlow)
            : (a <= 3 ? RateBitFast : a <= 7 ? RateFaster : RateVeryFast);
        var n = rate == 0 ? "0" : rate < 0 ? "\u2212" + a : "+" + a;
        return $"{word} ({n})";
    }
    /// <summary>« 80 % » (narrow no-break space before %, French typography).</summary>
    public static string VolumeValue(int volume) => $"{System.Math.Clamp(volume, 0, 100)}\u202F%";
    /// <summary>The slider's UI Automation name with its value: « Vitesse : Un peu lente (−2) ».</summary>
    public static string SliderName(string label, string value) => $"{label}\u00A0: {value}";
    public const string Listen = "Écouter";
    public const string ListenText = "Bonjour, je suis la voix de Pépite.";
    public const string LockedNote = "Réglé par l'administrateur";
    // reading of the selected text (plan 07, D21/D24)
    public const string SecReading = "Lecture";
    public const string ReadingEnabled = "Lire le te_xte sélectionné (barre de lecture)";
    public const string ReadingClipboard = "Copier la sélection par Ctrl+C si l'application ne la donne pas (presse-papiers rétabli) :";
    public const string ReadingPictures = "Images dans la barre de lecture";
    /// <summary>0.11.0 (owner 2026-11-27): the three positions of « Copier la sélection par Ctrl+C… » (never in Calc).</summary>
    public const string ReadingClipboardWriter = "Dans LibreOffice Writer seulement";
    public const string ReadingClipboardEverywhere = "Partout";
    public const string ReadingClipboardNever = "Jamais";
    public const string ReadingClipboardHint =
        "Envoie Ctrl+C à l'application, lit le texte copié, puis remet le presse-papiers comme avant (sans l'ajouter à " +
        "l'historique). La copie faite par l'application elle-même peut apparaître dans l'historique du presse-papiers " +
        "(Windows+V) : cela ne peut pas être évité. Jamais dans un champ de mot de passe, dans une console ni dans LibreOffice Calc.";
    public const string Save = "_Enregistrer";
    public const string Cancel = "Annuler";
    /// <summary>Words of the settings preview and of the bar warm-up (one with an accent: glyph cache).</summary>
    public static readonly string[] PreviewWords = { "maison", "chat", "été" };
    /// <summary>Glyph coverage test of the render sheets: every French letter (+ capitals via ToUpper).</summary>
    public const string GlyphTest = "aàâäbcçdeéèêëfghiîïjklmnoôöœpqrstuùûüvwxyÿz";

    // reading mode of the bar and « grande fenêtre » (plan 07, D21/D24)
    public const string ReadPlay = "Lire (Espace ou F9)";
    public const string ReadPause = "Pause (Espace ou F9)";
    public const string ReadPrev = "Phrase précédente";
    public const string ReadNext = "Phrase suivante";
    public const string ReadLarge = "Grande fenêtre";
    public const string ReadTooLong = "Texte trop long, seuls les 1000 premiers mots sont lus";
    public const string ReadTileHint = "Clic : écouter ce mot";
    public const string ReadWindowTitle = "Lecture — Pépite";
    public const string ReadingName = "Barre de lecture";

    // welcome window, « déjà démarré », ready notification (TODO « Start-up feedback », owner spec 2026-11-14).
    // Microsoft's own terms: « barre des tâches », « zone de notification »; plain words for the user.
    public const string WelcomeWindowTitle = "Bienvenue — Pépite";
    public const string AlreadyWindowTitle = "Pépite";
    public const string WelcomeHeading = "Pépite est prêt !";
    public const string AlreadyHeading = "Pépite est déjà démarré";
    public const string WelcomeBody =
        "Pépite fonctionne en arrière-plan. Écrivez dans n'importe quelle application : les mots s'affichent au-dessus du texte.";
    public const string WelcomeIconHint =
        "Pépite se règle avec son icône, à droite de la barre des tâches. Pour la garder visible : faites-la glisser près de l'horloge.";
    /// <summary>Windows 11 hides new icons behind the « ^ » arrow; the setting's wording checked on a fr-FR Windows 11
    /// (Paramètres > Personnalisation > Barre des tâches: « Autres icônes de barre d’état système », 26H2 build 26300).</summary>
    public const string WelcomeOverflowHint =
        "Si vous ne la voyez pas, cliquez sur la flèche « ^ ». Vous pouvez aussi l'afficher toujours : Paramètres > " +
        "Personnalisation > Barre des tâches > Autres icônes de barre d’état système.";
    /// <summary>The screen-reader name of the taskbar picture.</summary>
    public const string TaskbarPictureName =
        "Image : la droite de la barre des tâches, avec la flèche « ^ » des icônes cachées, l'icône de Pépite entourée, puis l'horloge.";
    public const string WelcomeMore = "En savoir plus";
    public const string AlreadySettings = "Ouvrir les Réglages";
    public const string AlreadyHelp = "Aide";
    public const string WelcomeOk = "OK";
    /// <summary>Neither the help pages nor the LISEZ-MOI could be opened (files missing, or no program for them).</summary>
    public const string HelpMissing = "L'aide n'a pas pu être ouverte : ses fichiers sont absents, ou aucun programme ne peut les afficher.";
    public const string HelpTitle = "Aide — Pépite";
    // the « ? » button of each Réglages section (F1 does the same while Réglages has the focus)
    public const string HelpButton = "?";
    public const string HelpButtonTip = "Aide sur cette partie (F1)";
    /// <summary>The screen-reader name of a « ? » button: « Aide : Voix ».</summary>
    public static string HelpButtonName(string section) => $"Aide : {section}";
    public const string ReadyBalloonTitle = "Pépite est prêt";
    public const string ReadyBalloonText = "Écrivez dans n'importe quelle application : les mots s'affichent au-dessus du texte.";
    // Réglages: the welcome message at start-up
    public const string SecStart = "Démarrage";
    /// <summary>The welcome is shown once (owner Q3, embedded-help plan): ticking this shows it at the next start.</summary>
    public const string ShowWelcome = "Afficher le message de bienvenue au prochain démarrage";

    // ------------------------------------------------------------------ profiles and teacher management (plan 09, D29)
    public const string DefaultProfileName = "Mon profil";
    public const string ClassProfileName = "Classe";
    public const string ProfileUnreadable = "(illisible)";
    /// <summary>The settings window title with the profile's name, when teacher management is on.</summary>
    public const string SettingsTitleFor = "Réglages — {0}";
    // Réglages, section « Plusieurs élèves »
    public const string SecPupils = "Plusieurs élèves";
    public const string ActivateManagement = "Activer la gestion enseignant…";
    public const string ActivateManagementHint = "Pour une classe : un profil par élève, protégé par un code enseignant.";
    public const string OpenTeacherSpace = "Ouvrir l'Espace enseignant…";
    public const string ManagementOnHint = "La gestion enseignant est activée.";
    /// <summary>The profiles folder cannot be written this session (in-memory « Mon profil »): no activation.</summary>
    public const string CannotSaveHint = "Les réglages ne peuvent pas être enregistrés pour le moment. Redémarrez Pépite.";
    // tray, management on
    public const string MenuProfile = "Profil : {0}";
    public const string MenuSwitchProfile = "Changer de profil…";
    public const string MenuTeacher = "Espace enseignant…";
    public const string TrayActiveFor = "Pépite ({0}) — F1–F8 pour insérer";
    public const string TrayPausedFor = "Pépite ({0}) — en pause";
    public const string TrayLoadingFor = "Pépite ({0}) — chargement de la voix {1}…";

    // the PIN dialog
    public const string PinTitle = "Code enseignant — Pépite";
    public const string PinCreateTitle = "Nouveau code enseignant — Pépite";
    public const string PinReasonEdit = "Le code de l'enseignant est demandé pour modifier les réglages.";
    public const string PinReasonSwitch = "Le code de l'enseignant est demandé pour changer de profil.";
    public const string PinReasonTeacher = "Le code de l'enseignant est demandé pour ouvrir l'Espace enseignant.";
    public const string PinReasonQuit = "Le code de l'enseignant est demandé pour quitter Pépite.";
    public const string PinReasonDeactivate = "Le code de l'enseignant est demandé pour désactiver la gestion enseignant.";
    public const string PinReasonChangeCode = "Tapez le code actuel de l'enseignant pour le changer.";
    public const string PinReasonNewRecovery = "Le code de l'enseignant est demandé pour créer un nouveau code de secours.";
    public const string PinLabel = "Code";
    public const string PinCreateText = "Choisissez le code de l'enseignant : 4 à 6 chiffres. Les élèves ne doivent pas le connaître.";
    public const string PinNewLabel = "Nouveau code";
    public const string PinConfirmLabel = "Le même code, encore une fois";
    public const string PinErase = "Effacer";
    public const string PinEraseName = "Effacer le dernier chiffre";
    public const string PinKeyName = "Chiffre {0}";
    public const string PinForgot = "Code oublié ?";
    public const string PinWrong = "Code incorrect.";
    public const string PinWrongCount = "Code incorrect ({0} essais).";
    public const string PinWait = "Trop d'essais. Réessayez dans {0}.";
    public const string PinChecking = "Vérification…";
    public const string PinProblemLength = "Le code doit avoir 4 à 6 chiffres.";
    public const string PinProblemDigits = "Le code ne contient que des chiffres.";
    public const string PinProblemWeak = "Ce code est trop facile à deviner. Choisissez-en un autre.";
    public const string PinProblemMismatch = "Les deux codes ne sont pas identiques.";
    public const string RecoveryPrompt = "Tapez le code de secours, imprimé ou noté quand le code enseignant a été créé :";
    public const string RecoveryLabel = "Code de secours";
    public const string RecoveryWrong = "Code de secours incorrect.";
    public const string RecoveryNoCode = "Sans le code de secours, l'administrateur de l'ordinateur peut remettre à zéro la gestion enseignant.";
    public const string Ok = "OK";
    // the recovery code, shown once
    public const string RecoveryTitle = "Code de secours — Pépite";
    public const string RecoveryHeading = "Code de secours";
    public const string RecoveryText =
        "Gardez ce code en lieu sûr, loin des élèves : il permet de choisir un nouveau code enseignant si vous l'oubliez. " +
        "Il n'est affiché qu'une seule fois.";
    public const string RecoveryPrint = "Imprimer…";
    public const string RecoveryCopy = "Copier";
    public const string RecoveryCopied = "Copié.";
    public const string RecoveryDone = "J'ai noté le code";
    public const string RecoveryPrintHeading = "Pépite — code de secours de l'Espace enseignant";
    public static string RecoveryPrintDate(System.DateTime d) => "Créé le " + d.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

    /// <summary>« 30 secondes », « 2 minutes 30 » (the wait of the PIN back-off).</summary>
    public static string Duration(int seconds)
    {
        if (seconds < 60) return seconds <= 1 ? "1 seconde" : $"{seconds} secondes";
        int m = seconds / 60, s = seconds % 60;
        var mins = m == 1 ? "1 minute" : $"{m} minutes";
        return s == 0 ? mins : $"{mins} {s:00}";
    }

    public static string PinReasonText(PinReason r) => r switch
    {
        PinReason.Switch => PinReasonSwitch,
        PinReason.Teacher => PinReasonTeacher,
        PinReason.Quit => PinReasonQuit,
        _ => PinReasonEdit,
    };

    public static string PinProblemText(PinProblem p) => p switch
    {
        PinProblem.NotDigits => PinProblemDigits,
        PinProblem.Weak => PinProblemWeak,
        PinProblem.Mismatch => PinProblemMismatch,
        _ => PinProblemLength,
    };

    // the Espace enseignant
    public const string TeacherTitle = "Espace enseignant — Pépite";
    public const string TabProfiles = "Profils";
    public const string TabLocking = "Verrouillage";
    public const string TabCode = "Code";
    public const string ProfilesCount = "{0} profils sur {1}";
    public const string ProfileListName = "Liste des profils";
    public const string ClassMark = "classe";
    public const string ActiveMark = "actif";
    public const string ProfileAdd = "Ajouter…";
    public const string ProfileEdit = "Nom et image…";
    public const string ProfileDuplicate = "Dupliquer…";
    public const string ProfileDelete = "Supprimer…";
    public const string ProfileUp = "Monter";
    public const string ProfileDown = "Descendre";
    public const string ProfileSettings = "Modifier les réglages…";
    public const string ProfileUse = "Utiliser maintenant";
    public const string ProfileFull = "40 profils au maximum (« Classe » comprise).";
    public const string ProfileClassHint = "« Classe » donne ses réglages à tous les élèves ; chaque élève ne garde que ses différences.";
    public const string CopyName = "{0} (copie)";
    public const string DeleteConfirm = "Supprimer le profil « {0} » ? Une sauvegarde est faite avant.";
    public const string DeleteActive = "Le profil utilisé maintenant ne peut pas être supprimé. Changez de profil d'abord.";
    public const string LockSwitch = "Code requis pour changer de profil";
    public const string LockEdit = "Code requis pour modifier les réglages du profil";
    public const string LockQuit = "Code requis pour quitter";
    public const string LockPicker = "Proposer « Qui es-tu ? » au début de la session Windows";
    public const string LockTeacherNote = "L'Espace enseignant demande toujours le code.";
    public const string CodeChange = "Changer le code…";
    public const string CodeNewRecovery = "Nouveau code de secours…";
    public const string CodeDeactivate = "Désactiver la gestion enseignant…";
    public const string CodeDeactivateConfirm =
        "Désactiver la gestion enseignant ? Les profils restent enregistrés, le profil actuel reste utilisé, et le code est supprimé. " +
        "Vous pourrez la réactiver plus tard (un nouveau code sera demandé).";
    public const string CodeChanged = "Le code enseignant a été changé.";
    public const string NewRecoveryConfirm =
        "Créer un nouveau code de secours ? L'ancien code de secours ne fonctionnera plus : notez ou imprimez le nouveau.";
    public const string EndSession = "Fin de session";
    public const string EndSessionHint = "Revenir à « Classe » et proposer « Qui es-tu ? ».";
    public const string Close = "Fermer";
    public const string Yes = "Oui";

    // Verrouillage: session returns and the profiles offered on this PC (plan 09B)
    public const string IdleReturnLabel = "Revenir à « Classe » après une inactivité de :";
    public const string IdleNever = "Jamais";
    public const string IdleMinutes = "{0} minutes";
    public const string ReturnOnLock = "Revenir à « Classe » quand la session Windows est verrouillée (Windows + L)";
    public const string LogoffNote = "À la fermeture de la session Windows, Pépite revient toujours à « Classe ».";
    public const string OfferedHeading = "Profils proposés sur ce poste";
    public const string OfferedOnly = "Ne proposer dans « Qui es-tu ? » que les élèves cochés, sur cet ordinateur ({0})";
    public const string OfferedListName = "Élèves proposés sur cet ordinateur";
    public const string OfferedHint = "Les nouveaux élèves sont ajoutés à la liste. Si aucun élève n'est coché, ou si aucun élève coché n'existe plus, tous les élèves sont proposés.";

    // Espace enseignant → Exporter / importer (plan 09B)
    public const string TabExchange = "Exporter / importer";
    public const string ExportHeading = "Exporter des profils";
    public const string ExportHelp = "Enregistre les profils cochés dans un fichier « .pepite », pour un autre ordinateur ou une sauvegarde. « Classe » est toujours enregistrée.";
    public const string ExportPrivacy = "Le fichier contient les prénoms des élèves : gardez-le en lieu sûr et ne l'envoyez pas par Internet.";
    public const string ExportListName = "Profils à exporter";
    public const string ExportAll = "Tout sélectionner";
    public const string ExportNone = "Aucun";
    public const string ExportIncludePin = "Inclure le code enseignant";
    public const string ExportIncludePinHint = "Seulement pour un autre ordinateur de la même classe : le code y sera proposé à l'import.";
    public const string ExportSave = "Enregistrer sous…";
    public const string ExportDone = "{0} profils enregistrés (avec « Classe »).";
    public const string ExportFailed = "Le fichier n'a pas pu être enregistré.";
    public const string ExportFileName = "profils-pepite-{0}";
    public const string FileFilter = "Profils Pépite (*.pepite)|*.pepite";
    public const string ImportHeading = "Importer des profils";
    public const string ImportHelp = "Ajoute ou remplace des profils à partir d'un fichier « .pepite ». Une sauvegarde est faite avant.";
    public const string ImportOpen = "Importer un fichier…";
    /// <summary>The French « remarque » written into every export (a teacher may open the file in Notepad).</summary>
    public const string ArchiveRemark = "Profils de Pépite (prénoms et réglages des élèves). Gardez ce fichier en lieu sûr.";
    // reading a file
    public const string ImportNotFound = "Ce fichier n'existe pas.";
    public const string ImportUnreadable = "Ce fichier ne peut pas être lu.";
    public const string ImportTooBig = "Ce fichier est trop gros (20 Mo au maximum) : ce n'est pas un fichier de profils Pépite.";
    public const string ImportNotPepite = "Ce fichier n'est pas un fichier de profils Pépite.";
    public const string ImportNewer = "Ce fichier vient d'une version plus récente de Pépite. Mettez Pépite à jour pour l'importer.";
    public const string ImportBadClass = "Les réglages de « Classe » de ce fichier ne peuvent pas être lus.";
    public const string ImportRowUnreadable = "ce profil ne peut pas être lu";
    public const string ImportRowBadSettings = "ses réglages ne peuvent pas être lus";
    public const string ImportTooMany = "{0} profils au maximum : décochez au moins {1} profil(s).";
    public const string ImportTooManyInFile = "Ce fichier contient trop de profils ({0} au maximum) : ce n'est pas un fichier de profils Pépite utilisable.";
    public const string ImportPinRefused = "Le code enseignant de ce fichier ne peut pas être utilisé : il n'est pas importé.";
    public const string ImportNothing = "Rien à importer : cochez au moins un profil.";
    public const string ImportBackupFailed = "La sauvegarde des profils a échoué : rien n'a été importé.";
    public const string ImportFailed = "L'import n'a pas pu se faire. {0}";
    // the import dialog
    public const string ImportTitle = "Importer des profils — Pépite";
    // plan 12 (Q3): a .pepite file opened from Explorer (double-click)
    public const string ImportNeedsManagement = "Pour importer des profils, activez d'abord la gestion enseignant dans Réglages.";
    public const string ImportOpenRefused = "Ce fichier ne peut pas être importé : seul un fichier « .pepite » de 50 Mo au plus peut être ouvert avec Pépite.";
    public const string ImportFileLabel = "Fichier : {0}";
    public const string ImportListName = "Profils du fichier";
    public const string ImportRowNew = "nouveau";
    public const string ImportRowExists = "ce prénom existe déjà";
    public const string ImportRowProblem = "ne peut pas être importé : {0}";
    public const string ImportRowName = "{0}, {1}";
    public const string ConflictReplace = "Remplacer";
    public const string ConflictKeepBoth = "Garder les deux";
    public const string ConflictSkip = "Ignorer";
    public const string ConflictChoiceName = "Que faire de {0}";
    public const string ImportForAll = "Pour tous les prénoms déjà présents :";
    public const string ImportForAllName = "Choix pour tous les prénoms déjà présents";
    public const string ImportReplaceClass = "Remplacer la classe de ce poste par celle du fichier";
    public const string ImportReplaceClassHint = "Tous les élèves de cet ordinateur reçoivent alors les réglages de « Classe » du fichier.";
    public const string ImportUsePin = "Utiliser le code enseignant du fichier";
    public const string ImportUsePinHint = "Le code enseignant de cet ordinateur (et son code de secours) sera remplacé par celui du fichier.";
    public const string ImportSummary = "{0} à créer, {1} à remplacer, {2} gardé(s) en double, {3} ignoré(s).";
    public const string ImportSummaryClass = " « Classe » sera remplacée.";
    public const string ImportSummaryPin = " Le code enseignant sera remplacé.";
    public const string ImportApply = "Importer…";
    public const string ImportConfirm = "Importer ces profils ?\n\n{0}\n\nUne sauvegarde des profils est faite avant.";
    public const string ImportDone = "Import terminé : {0} créé(s), {1} remplacé(s), {2} gardé(s) en double, {3} ignoré(s).";
    public const string ImportDoneClass = " « Classe » a été remplacée.";
    public const string ImportDonePin = " Le code enseignant a été remplacé.";

    // add / edit a profile: first name, pictogram, colour (no personal pictures, D29 Q9)
    public const string ProfileEditTitle = "Profil — Pépite";
    public const string ProfileNameLabel = "Prénom";
    public const string ProfilePictoLabel = "Image : un mot (chat, fusée…)";
    public const string ProfileNoPicto = "Pas d'image pour ce mot : l'initiale sera affichée.";
    public const string ProfilePictoMissing = "Pack d'images absent : l'initiale sera affichée.";
    public const string ProfileColorLabel = "Couleur";
    public const string ProfilePreview = "Aperçu";
    public const string NameEmpty = "Écrivez un prénom.";
    public const string NameTooLong = "30 caractères au maximum.";
    public const string NameControl = "Ce prénom contient un caractère non permis.";
    public const string NameDuplicate = "Ce prénom existe déjà.";
    public static string NameProblemText(NameProblem p) => p switch
    {
        NameProblem.TooLong => NameTooLong,
        NameProblem.ControlChars => NameControl,
        NameProblem.Duplicate => NameDuplicate,
        _ => NameEmpty,
    };
    public static string ColorName(ProfileColor c) => c switch
    {
        ProfileColor.Rouge => "Rouge",
        ProfileColor.Orange => "Orange",
        ProfileColor.Jaune => "Jaune",
        ProfileColor.Vert => "Vert",
        ProfileColor.Bleu => "Bleu",
        ProfileColor.Violet => "Violet",
        ProfileColor.Rose => "Rose",
        _ => "Marron",
    };

    // « Qui es-tu ? »
    public const string PickerTitle = "Qui es-tu ? — Pépite";
    public const string PickerHeading = "Qui es-tu ?";
    public const string PickerConfirm = "C'est moi ✓";
    public const string PickerHint = "Clique sur ton image, puis sur « C'est moi ». Échap : garder le profil actuel.";
    public const string PickerTileName = "Profil de {0}";
    public const string PickerTileHelp = "{0}, couleur {1}";

    // personal words (plan 10A): lists, « Mes fautes », lessons — words are added on request only
    public const string MenuAddWord = "Ajouter un mot…";
    public const string TabSettingsGeneral = "Barre et voix";
    public const string TabWords = "Mots";
    public const string MyWordsList = "Mes mots";
    public const string ClassWordsList = "Mots de la classe";
    public const string ClassWordsHeading = "Mots de la classe";
    public const string LessonDefaultName = "Leçon";
    public const string WordLabel = "_Mot :";
    public const string WordListen = "Écouter";
    public const string WordAdd = "_Ajouter";
    public const string WordSave = "_Enregistrer";
    public const string WordHint = "Une lettre majuscule est gardée (« Léa », « Vésuve »). Un trait d'union ou une apostrophe au milieu est accepté (« Jean-Pierre »).";
    public const string AddWordTitle = "Ajouter un mot — Pépite";
    public const string AddWordTitleFor = "Ajouter un mot pour {0} — Pépite";
    public const string EditWordTitle = "Modifier le mot — Pépite";
    public const string FauteTitle = "Mes fautes — Pépite";
    public const string FauteWritten = "_J'écris :";
    public const string FauteWanted = "Je _veux écrire :";
    public const string FauteHint = "Quand tu écris le premier mot (ou ses 3 premières lettres), Pépite propose le second en premier.";
    public const string WordSearch = "_Chercher :";
    /// <summary>« 1 mot dans cette liste ; 3 mots en tout (au plus 5000). » (French: singular for 0 and 1).</summary>
    public static string WordsCount(int inList, int total, int max) =>
        $"{Mots(inList)} dans cette liste ; {Mots(total)} en tout (au plus {max}).";
    public static string Mots(int n) => n >= 2 ? $"{n} mots" : $"{n} mot";
    public const string WordAddDots = "Ajouter…";
    public const string WordEdit = "Modifier…";
    public const string WordDelete = "Supprimer";
    public const string WordHide = "Ne pas me le proposer";
    public const string WordUnhide = "Me le proposer de nouveau";
    public const string WordHiddenMark = "{0} (caché)";
    // 0.11.0 (plan offensive-words B3, D35): « Classe » only, behind the PIN
    public const string ExclusHeading = "Mots à ne jamais proposer";
    public const string ExclusHelp = "Ces mots ne sont jamais proposés, à aucun élève de ce poste (en plus de la liste intégrée). Un élève qui en tape un l'écrit normalement.";
    public const string ExclusAddTitle = "Mot à ne jamais proposer — Pépite";
    public const string ExclusRemove = "Retirer";
    public const string ExclusAdded = "Ce mot ne sera plus proposé.";
    public const string ExclusRemoved = "Ce mot peut de nouveau être proposé.";
    public const string ExclusMachineMark = "{0} — " + LockedNote;
    public const string WordBannedMark = "{0} (jamais proposé)";
    public const string FautesHeading = "Mes fautes";
    public const string FautesHelp = "« J'écris … » → « Je veux écrire … » : le bon mot est proposé en premier.";
    public const string FauteRow = "{0} → {1}";
    public const string ListsHeading = "Listes et leçons";
    public const string ListsHelp = "Les mots d'une liste cochée sont proposés. Une leçon reste active jusqu'à ce qu'on la décoche.";
    public static string ListRow(string name, int n) => $"{name} ({Mots(n)})";
    public const string ListLessonMark = " — leçon";
    public const string LessonNew_ = "Nouvelle leçon…";
    public const string ListImportTxt = "Importer une liste (.txt)…";
    public const string ListExportTxt = "Exporter la liste (.txt)…";
    public const string ListDelete = "Supprimer la liste";
    public static string ListDeleteConfirm(string name, int n) => $"Supprimer la liste « {name} » ({Mots(n)}) ?";
    public const string ListChooseFirst = "Choisissez d'abord une liste.";
    public const string EraseAllWords = "Effacer tous mes mots…";
    public const string EraseClassWords = "Effacer tous les mots de la classe…";
    public const string EraseAllConfirm = "Effacer tous les mots de ce profil (ses listes, « Mes fautes », les mots cachés) ? Les mots de la classe ne changent pas. Une sauvegarde des profils est faite avant.";
    public const string EraseClassConfirm = "Effacer tous les mots de « Classe » (ses listes et « Mes fautes ») ? Les mots à ne jamais proposer sont gardés. Une sauvegarde des profils est faite avant.";
    public const string EraseDone = "Les mots ont été effacés (une sauvegarde a été faite avant).";
    public const string EraseFailed = "Les mots n'ont pas été effacés : la sauvegarde n'a pas pu être faite.";
    public const string WordAdded = "Mot ajouté.";
    public const string WordDeleted = "Mot supprimé.";
    public const string WordHiddenDone = "Ce mot de la classe ne sera plus proposé.";
    public const string WordShownDone = "Ce mot de la classe est de nouveau proposé.";
    public const string LessonTitle = "Nouvelle leçon — Pépite";
    public const string LessonNameLabel = "_Nom de la leçon :";
    public const string LessonTextLabel = "_Texte de la leçon (collez-le ici) :";
    public const string LessonTextNote = "Le texte n'est jamais enregistré : seuls les mots cochés sont gardés.";
    public const string LessonOpenTxt = "_Ouvrir un fichier .txt…";
    public const string LessonFind = "_Chercher les mots";
    public const string LessonWordsName = "Mots de la leçon";
    public const string LessonTickAll = "Tout cocher";
    public const string LessonTickNone = "Tout décocher";
    public const string LessonSave = "Enregistrer la _liste";
    public const string LessonNew = "nouveau";
    public const string LessonKnown = "connu";
    public const string LessonNoWords = "Aucun mot trouvé. Collez un texte, puis « Chercher les mots ».";
    public static string LessonCount(int found, int ticked) =>
        $"{Mots(found)} {(found >= 2 ? "trouvés" : "trouvé")}, {ticked} {(ticked >= 2 ? "cochés" : "coché")}.";
    public static string LessonAdded(int n) => $"Leçon ajoutée : {Mots(n)}.";
    public const string TxtFilter = "Liste de mots (*.txt)|*.txt";
    public const string TxtTooBig = "Ce fichier est trop gros (1 Mo au plus).";
    public const string TxtUnreadable = "Ce fichier ne peut pas être lu.";
    public const string TxtNoWords = "Ce fichier ne contient aucun mot utilisable (un mot par ligne).";
    public static string TxtImported(int n, int refused) =>
        $"{Mots(n)} {(n >= 2 ? "importés" : "importé")} ({refused} {(refused >= 2 ? "lignes refusées" : "ligne refusée")}).";
    public static string TxtExported(int n) => $"{Mots(n)} {(n >= 2 ? "exportés" : "exporté")}.";
    public const string TxtNotWritten = "Le fichier n'a pas pu être enregistré.";
    public const string WordsProfileLabel = "_Profil :";
    public const string ExportIncludeWords = "Inclure les listes de mots";
    public const string ExportIncludeWordsHint = "Les listes de mots, « Mes fautes » et les mots cachés de chaque profil exporté.";
    public const string ImportClassWordsBad = "Les mots de « Classe » de ce fichier ne peuvent pas être lus (ou sont trop nombreux).";
    public const string ImportRowBadWords = "ses mots ne peuvent pas être lus (ou sont trop nombreux)";
    public static string WordProblemText(WordProblem p) => p switch
    {
        WordProblem.TooLong => "Un mot a au plus 40 lettres.",
        WordProblem.BadChars => "Un mot ne contient que des lettres, avec au besoin un trait d'union ou une apostrophe au milieu (pas d'espace, pas de chiffre).",
        WordProblem.Duplicate => "Ce mot est déjà dans la liste.",
        WordProblem.TooManyWords => "La liste est pleine (5 000 mots pour un élève, 10 000 pour la classe).",
        WordProblem.TooManyInList => "Une leçon a au plus 500 mots.",
        WordProblem.TooManyLists => "Un profil a au plus 50 listes.",
        WordProblem.TooManyFautes => "Au plus 500 « Mes fautes ».",
        WordProblem.SamePair => "Les deux mots sont identiques.",
        WordProblem.TooManyExclus => "Au plus 500 mots à ne jamais proposer.",
        WordProblem.ClassOnly => "Seuls les réglages de « Classe » ont des mots à ne jamais proposer.",
        _ => "Écrivez un mot.",
    };

    // spelling indicator (plan 10B, D12): the round marker left of the words; never a ✗
    public const string SpellKnownName = "Orthographe : mot connu";
    public const string SpellSoundsName = "Orthographe : s'écrit peut-être autrement";
    public const string SpellUnknownName = "Orthographe : mot inconnu";
    public const string SpellSoundsAction = "clic : voir les mots qui se prononcent pareil";
    public const string SpellUnknownAction = "clic : ajouter ce mot à mes mots";
    public const string SpellActionSep = " — ";
    public const string SpellKeyF9 = " (F9)";
    public const string SpellListen = " — clic droit : écouter";
    public static string AddSpelledTitle(string word) => $"Ajouter « {word} » à mes mots ?";
    public const string AddSpelledOk = "_Oui";
    public const string AddSpelledNo = "Non";
    public const string SpellingIndicatorLabel = "_Indicateur d'orthographe";
    public const string SpellingIndicatorHint =
        "Un rond à gauche des mots proposés : ✓ vert = mot connu, ≈ bleu = s'écrit peut-être autrement (clic : les mots qui se " +
        "prononcent pareil), ? rouge = mot inconnu (clic : l'ajouter à mes mots). Rien de ce qui est écrit n'est enregistré.";

    // proper names (plan 10C): the « Noms propres » pack, pictures of names, the teacher's names databases
    public const string NamePicturesLabel = "Images pour les noms propres";
    public const string NamePicturesHint =
        "Le drapeau d'un pays, d'une région ou d'une province, ou l'image donnée par l'enseignant, au-dessus d'un nom propre. " +
        "Un nom propre ne prend jamais l'image d'un mot ordinaire (« Pierre » n'a pas l'image d'une pierre).";
    public const string AboutNamesHeader = "Noms propres";
    public const string AboutNamesMissing = "Noms propres : paquet absent (prénoms, pays et villes non proposés).";
    public const string AboutNamesSource = "Paquet de données ouvertes « Noms propres » (licence CC BY 4.0) ; sources et auteurs : dossier noms (SOURCES.md).";
    public const string NamesImportButton = "Importer une base de noms…";
    public const string NamesImportTitle = "Importer une base de noms — Pépite";
    /// <summary>The file dialog's title: a database folder is imported by picking its noms.json / noms.csv.</summary>
    public const string NamesImportPick = "Choisissez le fichier .zip de la base, ou noms.json / noms.csv dans son dossier";
    public const string NamesImportFilter = "Base de noms (*.zip;*.json;*.csv)|*.zip;*.json;*.csv";
    public const string NamesImportRule =
        "Images : seulement des pictogrammes ou des dessins, jamais de photos (en particulier pas de visages d'élèves). " +
        "Vous êtes responsable des droits des images : la licence de chaque image est demandée, gardée et affichée.";
    public const string NamesImportCsvLicence = "Licence des images de ce fichier (si une ligne n'en donne pas) :";
    public const string NamesImportTo = "Ces noms iront dans les listes de : {0}";
    public static string NamesImportSummary(string title, int names, int images) =>
        $"« {title} » : {(names >= 2 ? names + " noms" : names + " nom")}, {(images >= 2 ? images + " images" : images + " image")}.";
    public const string NamesImportLicences = "Licence(s) : {0}";
    public const string NamesImportNoLicence = "aucune (les images sans licence sont ignorées)";
    public const string NamesImportRefused = "Lignes ou images laissées de côté :";
    public const string NamesImportRunning = "Import en cours… (les images sont préparées)";
    public const string NamesImportOk = "_Importer";
    public const string NamesImportCancel = "Annuler";
    public static string NamesImportDone(int names, int images) =>
        $"Base de noms importée : {(names >= 2 ? names + " noms" : names + " nom")}, {(images >= 2 ? images + " images" : images + " image")}.";
    public const string NamesImportFailed = "Cette base de noms ne peut pas être importée : {0}";
    public const string NamesImportEmpty = "Aucun nom utilisable dans cette base.";
    public const string NamesListMark = " — noms";
    public const string ExportIncludeImages = "Inclure les images";
    public const string ExportIncludeImagesHint = "Les images des bases de noms importées (le fichier devient une archive .pepite de 50 Mo au plus).";
    public const string ExportImagesTooBig = "Les images dépassent 50 Mo : exportez sans « Inclure les images » ou avec moins de profils.";
    public const string NamesListLicence = "Licence des images : {0}";
    // reasons a picture of a teacher's names database is left out (NamesImport)
    public const string NamesImageNoLicence = "« {0} » : image sans licence, ignorée (le nom est gardé)";
    public const string NamesImageTooMany = "« {0} » : plus de {1} images pour ce profil, image ignorée";
    public const string NamesImageMissing = "image absente, hors de la base ou de plus de 5 Mo";
    public const string NamesImageTooBig = "image de plus de 5 Mo";
    public const string NamesImageFormat = "image ni PNG ni JPEG";
    public const string NamesImagePixels = "image de {0} × {1} pixels (4096 au plus)";
    public const string NamesImageUnreadable = "« {0} » : image illisible";
    public const string NamesImageProblem = "« {0} » : {1}";
    public const string NamesOpenNotFound = "fichier introuvable";
    public const string NamesOpenWhat = "choisissez un dossier, un fichier .zip, noms.json ou noms.csv";
    public const string NamesOpenNoIndex = "ni noms.json ni noms.csv";
    public const string NamesOpenTooBig = "archive ou fichier de plus de 50 Mo";

    // errors
    public const string DbNotFound = "prediction.sqlite introuvable.\nPassez son chemin en argument, ou placez-le à côté de Pepite.exe.";
}
