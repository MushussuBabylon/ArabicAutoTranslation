using System;
using System.Collections.Generic;
using System.Linq;
using ArabicAutoTranslation.Services;
using RimWorld;
using UnityEngine;
using Verse;
using ArabicIntegration = ArabicAutoTranslation.Arabic.ArabicIntegration;

namespace ArabicAutoTranslation.UI
{
    public class Dialog_GistMerge : Window
    {
        private Dictionary<string, MergeItem> mergeItems;
        private Vector2 scrollPosition;
        private HashSet<string> selectedKeys = new HashSet<string>();
        
        private const float WindowWidth = 1000f;
        private const float WindowHeight = 700f;
        private const float RowHeight = 40f;
        
        public override Vector2 InitialSize => new Vector2(WindowWidth, WindowHeight);
        
        public class MergeItem
        {
            public string Key;
            public string LocalTranslation;
            public string RemoteTranslation;

            // the texts as the game will show them (Arabic: shaped and reordered); worked out when first drawn
            private string _localShown;
            private string _remoteShown;
            public string LocalShown => _localShown ?? (_localShown = Shown(LocalTranslation));
            public string RemoteShown => _remoteShown ?? (_remoteShown = Shown(RemoteTranslation));

            private static string Shown(string text)
            {
                if (!ArabicIntegration.Active || string.IsNullOrEmpty(text)) return text;
                var shown = ArabicIntegration.Preview(text);
                return string.IsNullOrEmpty(shown) ? text : shown;
            }
            public bool IsNew; // whether this is a new translation that does not exist locally
        }
        
        public Dialog_GistMerge(Dictionary<string, string> remoteTranslations)
        {
            mergeItems = new Dictionary<string, MergeItem>();
            
            // compare with the local cache and extract the differences
            foreach (var remote in remoteTranslations)
            {
                var key = remote.Key;
                var remoteValue = remote.Value;
                
                if (TranslatorManager.CachedTranslationsV2.TryGetValue(key, out var localValue))
                {
                    // for Arabic the entries are compared (and shown) as plain text
                    localValue = TranslatorManager.EditableTextOf(key, localValue);
                    // merge when the value differs
                    if (localValue != remoteValue)
                    {
                        mergeItems[key] = new MergeItem
                        {
                            Key = key,
                            LocalTranslation = localValue,
                            RemoteTranslation = remoteValue,
                            IsNew = false
                        };
                        selectedKeys.Add(key); // select everything by default
                    }
                }
                else
                {
                    // new translation that does not exist locally
                    mergeItems[key] = new MergeItem
                    {
                        Key = key,
                        LocalTranslation = "",
                        RemoteTranslation = remoteValue,
                        IsNew = true
                    };
                    selectedKeys.Add(key); // select everything by default
                }
            }
            
            doCloseX = true;
            doCloseButton = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
        }
        
        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            
            float currentY = 0f;
            
            // Title
            Text.Font = GameFont.Medium;
            var titleRect = new Rect(0f, currentY, inRect.width, 35f);
            Widgets.Label(titleRect, "AT_Gist_MergeTitle".Translate());
            Text.Font = GameFont.Small;
            currentY += 40f;
            
            // Info
            var infoRect = new Rect(0f, currentY, inRect.width, 25f);
            Widgets.Label(infoRect, $"{"AT_Gist_MergeInfo".Translate()}: {mergeItems.Count}");
            currentY += 30f;
            
            // Buttons
            var buttonRect = new Rect(0f, currentY, inRect.width, 30f);
            var selectAllRect = new Rect(buttonRect.x, buttonRect.y, 120f, 28f);
            var deselectAllRect = new Rect(selectAllRect.xMax + 5f, buttonRect.y, 120f, 28f);
            var selectNewOnlyRect = new Rect(deselectAllRect.xMax + 5f, buttonRect.y, 180f, 28f);

            if (Widgets.ButtonText(selectAllRect, "AT_Gist_SelectAll".Translate()))
            {
                selectedKeys.Clear();
                foreach (var key in mergeItems.Keys)
                {
                    selectedKeys.Add(key);
                }
            }

            if (Widgets.ButtonText(deselectAllRect, "AT_Gist_DeselectAll".Translate()))
            {
                selectedKeys.Clear();
            }

            // "Keep my existing translations": only translations I don't have yet
            if (Widgets.ButtonText(selectNewOnlyRect, "AT_Gist_SelectNewOnly".Translate()))
            {
                selectedKeys.Clear();
                foreach (var item in mergeItems.Values)
                {
                    if (item.IsNew) selectedKeys.Add(item.Key);
                }
            }
            
            currentY += 35f;
            
            // List
            var listRect = new Rect(0f, currentY, inRect.width, inRect.height - currentY - 50f);
            DrawMergeList(listRect);
            
            // Bottom buttons
            var bottomY = inRect.height - 45f;
            var mergeRect = new Rect(inRect.width - 160f, bottomY, 150f, 35f);
            var cancelRect = new Rect(mergeRect.x - 160f, bottomY, 150f, 35f);
            
            if (Widgets.ButtonText(mergeRect, "AT_Gist_MergeSelected".Translate()))
            {
                ApplyMerge();
                Close();
            }
            
            if (Widgets.ButtonText(cancelRect, "Cancel".Translate()))
            {
                Close();
            }
        }
        
        private void DrawMergeList(Rect rect)
        {
            if (mergeItems.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(rect, "AT_Gist_NoDifferences".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }
            
            // Column headers
            var headerRect = new Rect(rect.x, rect.y, rect.width, 25f);
            DrawColumnHeaders(headerRect);
            
            var contentRect = new Rect(rect.x, rect.y + 30f, rect.width, rect.height - 30f);
            var items = mergeItems.Values.OrderBy(x => x.Key).ToList();
            
            float totalHeight = items.Count * RowHeight;
            var viewRect = new Rect(0f, 0f, contentRect.width - 16f, totalHeight);
            
            Widgets.BeginScrollView(contentRect, ref scrollPosition, viewRect);
            
            float currentY = 0f;
            foreach (var item in items)
            {
                var rowRect = new Rect(0f, currentY, viewRect.width, RowHeight);
                
                if (Mouse.IsOver(rowRect))
                {
                    Widgets.DrawHighlight(rowRect);
                }
                
                DrawMergeRow(rowRect, item);
                currentY += RowHeight;
            }
            
            Widgets.EndScrollView();
        }
        
        private void DrawColumnHeaders(Rect rect)
        {
            var prevAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            
            var checkboxRect = new Rect(rect.x, rect.y, 30f, rect.height);
            var keyRect = new Rect(checkboxRect.xMax, rect.y, rect.width * 0.25f, rect.height);
            var localRect = new Rect(keyRect.xMax, rect.y, rect.width * 0.3f, rect.height);
            var remoteRect = new Rect(localRect.xMax, rect.y, rect.width * 0.3f, rect.height);
            
            Widgets.Label(keyRect, "AT_Gist_Key".Translate());
            Widgets.Label(localRect, "AT_Gist_LocalTranslation".Translate());
            Widgets.Label(remoteRect, "AT_Gist_RemoteTranslation".Translate());
            
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width);
            
            Text.Anchor = prevAnchor;
        }
        
        private void DrawMergeRow(Rect rect, MergeItem item)
        {
            var checkboxRect = new Rect(rect.x + 5f, rect.y, 25f, RowHeight);
            var isSelected = selectedKeys.Contains(item.Key);
            var newIsSelected = isSelected;
            
            Widgets.Checkbox(checkboxRect.position, ref newIsSelected);
            
            if (newIsSelected != isSelected)
            {
                if (newIsSelected)
                {
                    selectedKeys.Add(item.Key);
                }
                else
                {
                    selectedKeys.Remove(item.Key);
                }
            }
            
            var keyRect = new Rect(checkboxRect.xMax + 5f, rect.y, rect.width * 0.25f - 35f, RowHeight);
            var localRect = new Rect(keyRect.xMax + 5f, rect.y, rect.width * 0.3f - 5f, RowHeight);
            var remoteRect = new Rect(localRect.xMax + 5f, rect.y, rect.width * 0.3f - 5f, RowHeight);
            
            // Key (show only the ModId part)
            var colonIndex = item.Key.IndexOf(':');
            var displayKey = colonIndex > 0 && colonIndex < 50 
                ? item.Key.Substring(0, Math.Min(colonIndex, 30)) + "..." 
                : item.Key.Substring(0, Math.Min(item.Key.Length, 30));
            
            Widgets.Label(keyRect.ContractedBy(2f), displayKey);
            TooltipHandler.TipRegion(keyRect, item.Key);
            
            // Local translation
            if (item.IsNew)
            {
                GUI.color = Color.gray;
                Widgets.Label(localRect.ContractedBy(2f), "AT_Gist_NewTranslation".Translate());
                GUI.color = Color.white;
            }
            else
            {
                Widgets.Label(localRect.ContractedBy(2f), item.LocalShown);
            }
            TooltipHandler.TipRegion(localRect, item.LocalShown);
            
            // Remote translation
            GUI.color = new Color(0.5f, 1f, 0.5f); // Light green
            Widgets.Label(remoteRect.ContractedBy(2f), item.RemoteShown);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(remoteRect, item.RemoteShown);
        }
        
        private void ApplyMerge()
        {
            int count = 0;
            int rejected = 0;
            var updated = new List<KeyValuePair<string, string>>();
            foreach (var key in selectedKeys)
            {
                if (mergeItems.TryGetValue(key, out var item))
                {
                    if (ArabicIntegration.Active)
                    {
                        // shared text is plain Arabic; it goes through the Arabic pipeline and the game gets the display string
                        string stored, error;
                        if (TranslatorManager.StoreEditedText(key, item.RemoteTranslation, out stored, out error))
                        {
                            updated.Add(new KeyValuePair<string, string>(key, stored));
                            count++;
                        }
                        else
                        {
                            rejected++;
                        }
                        continue;
                    }

                    // Update both caches
                    TranslationCacheManager.AddOrUpdate(key, item.RemoteTranslation);
                    TranslatorManager.CachedTranslationsV2[key] = item.RemoteTranslation;
                    updated.Add(new KeyValuePair<string, string>(key, item.RemoteTranslation));
                    count++;
                }
            }

            // Save to disk
            TranslationCacheManager.Save(nameof(TranslatorManager.CachedTranslationsV2));

            // Apply to the LIVE game immediately - no restart needed
            var applied = InjectionManager.ReapplyTranslations(updated);
            if (applied > 0)
            {
                Settings.ResetDefCaches();
            }

            Messages.Message($"{"AT_Gist_MergeSuccess".Translate()} ({count}, {"AT_Gist_AppliedLive".Translate()}: {applied})", MessageTypeDefOf.PositiveEvent);
            if (rejected > 0)
            {
                Messages.Message("AT_Arabic_MergeRejected".Translate(rejected.ToString()), MessageTypeDefOf.RejectInput);
            }
        }
    }
}


