using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArabicAutoTranslation.Services;
using ArabicAutoTranslation.Utilities;
using HarmonyLib;
using UnityEngine;
using Verse;
using ArabicIntegration = ArabicAutoTranslation.Arabic.ArabicIntegration;

namespace ArabicAutoTranslation
{
    /*
     * TODO LIST:
     * 1. ruleStrings and ruleFiles must not simply be fed to the translator as they are.
     * Example ruleStrings: for maybe_adjective->[Color], split at -> and translate only the right-hand side. The strings inside [] are temporarily replaced with placeholders such as {0} and restored afterwards...
     * Example ruleFiles: businessname->Names/Business must not simply be translated. It has to be translated later, when the ruleFiles are parsed.
     * 2. Translate Keyed and Strings
     */
    public class ArabicAutoTranslation : Mod
    {
        internal static readonly Stopwatch sw = new Stopwatch();
        internal const string LogPrefix = "<color=#34e2eb>Arabic Auto Translation</color>: ";

        private Settings settings;

        public ArabicAutoTranslation(ModContentPack content) : base(content)
        {
            settings = GetSettings<Settings>();

            // the bundled Arabic glossary is read from this mod's folder
            ArabicIntegration.ModRoot = content.RootDir;

            // Perform migration for existing users
            MigrationHelper.PerformMigration();

            var h = new Harmony("mushussubabylon.arabicautotranslation");
            h.PatchAll();
            Log.Message(LogPrefix + "Harmony patches are applied!");

            TranslatorManager.Prepare();
            TranslatorManager.StartThread();
        }

        public override string SettingsCategory() => "AT_Name".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);

            settings.DoSettingsWindowContents(inRect);
        }
    }
}
