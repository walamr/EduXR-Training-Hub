using UnityEngine;
using UnityEngine.UI;

namespace XRMultiplayer
{
    /// <summary>
    /// Repairs voting host UI layout (center-anchored 100x100 rects stacked on top of each other).
    /// </summary>
    public static class VotingUILayoutUtility
    {
        public static void StretchToParent(RectTransform rect)
        {
            if (rect == null)
                return;

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;
        }

        /// <summary>
        /// Configures a row for use inside a VerticalLayoutGroup (top-aligned stretch width).
        /// </summary>
        public static void ApplyLayoutRow(RectTransform rect, float preferredHeight, bool flexibleWidth = true)
        {
            if (rect == null)
                return;

            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(0f, preferredHeight);

            var le = rect.GetComponent<LayoutElement>();
            if (le == null)
                le = rect.gameObject.AddComponent<LayoutElement>();

            le.minHeight = preferredHeight;
            le.preferredHeight = preferredHeight;
            le.flexibleHeight = 0;
            le.flexibleWidth = flexibleWidth ? 1 : 0;
        }

        public static void ConfigureHostPanel(
            RectTransform hostPanelRect,
            GameObject multiChoiceTabContent,
            GameObject handRaiseTabContent,
            Transform optionsContainer,
            Transform bottomButtonsRow)
        {
            if (hostPanelRect != null)
            {
                var hostVlg = hostPanelRect.GetComponent<VerticalLayoutGroup>();
                if (hostVlg != null)
                {
                    hostVlg.spacing = 8;
                    hostVlg.padding = new RectOffset(12, 12, 12, 12);
                    hostVlg.childControlWidth = true;
                    hostVlg.childControlHeight = true;
                    hostVlg.childForceExpandWidth = true;
                    hostVlg.childForceExpandHeight = false;
                }

                for (int i = 0; i < hostPanelRect.childCount; i++)
                {
                    var child = hostPanelRect.GetChild(i) as RectTransform;
                    if (child == null)
                        continue;

                    if (child.name == "TabContent")
                    {
                        var tabLe = child.GetComponent<LayoutElement>() ?? child.gameObject.AddComponent<LayoutElement>();
                        tabLe.minHeight = 260;
                        tabLe.preferredHeight = 300;
                        tabLe.flexibleHeight = 1;
                        
                        // Treat TabContent as a row in the vertical layout stack
                        child.anchorMin = new Vector2(0f, 1f);
                        child.anchorMax = new Vector2(1f, 1f);
                        child.pivot = new Vector2(0.5f, 1f);
                        child.anchoredPosition = Vector2.zero;
                        child.sizeDelta = new Vector2(0f, 300f);
                    }
else if (child.name == "BottomButtons")
                    {
                        ApplyLayoutRow(child, 48);
                    }
                    else if (child.name == "TabButtons")
                    {
                        ApplyLayoutRow(child, 40);
                    }
                    else if (child.name == "Title")
                    {
                        ApplyLayoutRow(child, 28);
                    }
                }
            }

            if (multiChoiceTabContent != null)
                ConfigureMultiChoiceTab(multiChoiceTabContent.transform, optionsContainer);

            if (handRaiseTabContent != null)
                StretchToParent(handRaiseTabContent.GetComponent<RectTransform>());

            if (bottomButtonsRow != null)
            {
                EnsureBottomButtonsOutsideTabContent(bottomButtonsRow, multiChoiceTabContent);
                ApplyLayoutRow(bottomButtonsRow as RectTransform, 48);

                var hlg = bottomButtonsRow.GetComponent<HorizontalLayoutGroup>();
                if (hlg == null)
                    hlg = bottomButtonsRow.gameObject.AddComponent<HorizontalLayoutGroup>();
                hlg.spacing = 12;
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.childForceExpandWidth = true;
                hlg.childForceExpandHeight = true;

                for (int i = 0; i < bottomButtonsRow.childCount; i++)
                    ApplyLayoutRow(bottomButtonsRow.GetChild(i) as RectTransform, 44);
            }
        }

        static void EnsureBottomButtonsOutsideTabContent(Transform bottomButtonsRow, GameObject multiChoiceTabContent)
        {
            if (bottomButtonsRow == null || multiChoiceTabContent == null)
                return;

            if (bottomButtonsRow.parent == multiChoiceTabContent.transform)
            {
                var hostPanel = multiChoiceTabContent.transform.parent?.parent;
                if (hostPanel != null)
                    bottomButtonsRow.SetParent(hostPanel, false);
            }

            bottomButtonsRow.SetAsLastSibling();
        }

        static void ConfigureMultiChoiceTab(Transform multiChoiceRoot, Transform optionsContainer)
        {
            if (multiChoiceRoot == null)
                return;

            StretchToParent(multiChoiceRoot as RectTransform);

            var vlg = multiChoiceRoot.GetComponent<VerticalLayoutGroup>();
            if (vlg == null)
                vlg = multiChoiceRoot.gameObject.AddComponent<VerticalLayoutGroup>();

            vlg.spacing = 6;
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            for (int i = 0; i < multiChoiceRoot.childCount; i++)
            {
                var child = multiChoiceRoot.GetChild(i) as RectTransform;
                if (child == null)
                    continue;

                if (optionsContainer != null && child == optionsContainer)
                {
                    ApplyLayoutRow(child, 72);
                    ConfigureOptionsList(optionsContainer);
                    continue;
                }

                float height = InferRowHeight(child.name);
                ApplyLayoutRow(child, height);
            }

            if (optionsContainer != null)
            {
                for (int i = 0; i < optionsContainer.childCount; i++)
                {
                    var optRow = optionsContainer.GetChild(i) as RectTransform;
                    if (optRow != null && optRow.gameObject.activeSelf)
                        ApplyLayoutRow(optRow, 30);
                }
            }
        }

        static float InferRowHeight(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return 28f;

            if (objectName.Contains("Label"))
                return 22f;
            if (objectName.Contains("QuickPolls"))
                return 36f;
            if (objectName.Contains("QuestionInput"))
                return 34f;
            if (objectName.Contains("AddRemove"))
                return 32f;
            if (objectName.Contains("Options"))
                return 72f;

            return 28f;
        }

        public static void ConfigureOptionsList(Transform optionsContainer)
        {
            if (optionsContainer == null)
                return;

            var layout = optionsContainer.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
                layout = optionsContainer.gameObject.AddComponent<VerticalLayoutGroup>();

            layout.spacing = 4;
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;

            var fitter = optionsContainer.GetComponent<ContentSizeFitter>();
            if (fitter == null)
                fitter = optionsContainer.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            var layoutElement = optionsContainer.GetComponent<LayoutElement>();
            if (layoutElement == null)
                layoutElement = optionsContainer.gameObject.AddComponent<LayoutElement>();
            layoutElement.minHeight = 64;
            layoutElement.preferredHeight = 72;
            layoutElement.flexibleHeight = 0;
        }

        public static void RebuildHostPanel(RectTransform hostPanelRect)
        {
            if (hostPanelRect == null)
                return;

            LayoutRebuilder.ForceRebuildLayoutImmediate(hostPanelRect);
            Canvas.ForceUpdateCanvases();
        }
    }
}
