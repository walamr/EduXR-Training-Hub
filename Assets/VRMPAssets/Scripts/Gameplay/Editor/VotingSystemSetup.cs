#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using Unity.Netcode;
using TMPro;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using System.Collections.Generic;

namespace XRMultiplayer
{
    /// <summary>
    /// Editor wizard to setup the Voting System with tabbed host panel.
    /// </summary>
    public class VotingSystemSetup : EditorWindow
    {
        // ========== DESIGN SYSTEM COLORS ==========
        private static readonly Color PanelBackground = new Color(0.106f, 0.106f, 0.106f, 0.95f);
        private static readonly Color ButtonNormal = new Color(0.18f, 0.18f, 0.18f, 1f);
        private static readonly Color ButtonAction = new Color(0.125f, 0.588f, 0.953f, 1f);
        private static readonly Color TabActive = new Color(0.125f, 0.588f, 0.953f, 1f);
        private static readonly Color TabInactive = new Color(0.25f, 0.25f, 0.25f, 1f);
        private static readonly Color StatusGreen = new Color(0.13f, 0.8f, 0.4f, 1f);
        private static readonly Color RecordingRed = new Color(0.9f, 0.2f, 0.2f, 1f);
        private static readonly Color InputFieldBg = new Color(0.12f, 0.12f, 0.12f, 1f);
        private static readonly string ROUNDED_SPRITE_PATH = "Assets/VRMPAssets/Textures/UI/Round Radius 10.png";

        private Transform tableTransform;
        private float canvasScale = 0.001f;

        [MenuItem("VRMP/Setup Voting System")]
        public static void ShowWindow()
        {
            GetWindow<VotingSystemSetup>("Voting System Setup");
        }

        private void OnGUI()
        {
            GUILayout.Label("Voting System Setup Wizard", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Creates VotingManager with tabbed host UI.", MessageType.Info);
            EditorGUILayout.Space();

            tableTransform = (Transform)EditorGUILayout.ObjectField("Table Transform", tableTransform, typeof(Transform), true);
            canvasScale = EditorGUILayout.FloatField("Canvas Scale", canvasScale);
            EditorGUILayout.Space();

            if (GUILayout.Button("Setup Voting System", GUILayout.Height(40)))
            {
                SetupSystem();
            }
        }

        private void SetupSystem()
        {
            Sprite roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(ROUNDED_SPRITE_PATH);
            TMP_FontAsset tmpFont = LoadTMPFont();

            // Root
            GameObject rootGO = new GameObject("VotingSystem_Root");
            Undo.RegisterCreatedObjectUndo(rootGO, "Create Voting System");
            rootGO.AddComponent<NetworkObject>();
            var votingManager = rootGO.AddComponent<VotingManager>();

            // Host Panel Canvas
            GameObject hostCanvasGO = CreateCanvas("VotingHostCanvas", rootGO.transform, canvasScale, new Vector2(400, 450));
            GameObject hostPanel = CreateHostPanel(hostCanvasGO.transform, roundedSprite, tmpFont);

            // VotingHUD
            GameObject hudGO = new GameObject("VotingHUD");
            hudGO.transform.SetParent(rootGO.transform);
            var votingHUD = hudGO.AddComponent<VotingHUD>();
            GameObject hudCanvas = CreateHUDCanvas(hudGO.transform, canvasScale, roundedSprite, tmpFont);

            // 3D Chart Origin
            GameObject chartOrigin = new GameObject("VotingChart");
            if (tableTransform != null)
            {
                chartOrigin.transform.SetParent(tableTransform);
                chartOrigin.transform.localPosition = new Vector3(0, 0.1f, 0);
            }

            // --- Configure VotingManager ---
            SerializedObject so = new SerializedObject(votingManager);
            if (tableTransform != null) so.FindProperty("m_TableTransform").objectReferenceValue = tableTransform;
            so.FindProperty("m_HostPanel").objectReferenceValue = hostPanel;
            so.FindProperty("m_ChartOrigin").objectReferenceValue = chartOrigin.transform;
            
            // Wire UI elements
            WireHostPanelReferences(so, hostPanel);
            so.ApplyModifiedProperties();

            // Configure VotingHUD
            SerializedObject soHUD = new SerializedObject(votingHUD);
            soHUD.FindProperty("m_HUDRoot").objectReferenceValue = hudCanvas;
            soHUD.FindProperty("m_QuestionText").objectReferenceValue = hudCanvas.transform.Find("Panel/QuestionText")?.GetComponent<TMP_Text>();
            soHUD.FindProperty("m_OptionsContainer").objectReferenceValue = hudCanvas.transform.Find("Panel/OptionsContainer");
            soHUD.FindProperty("m_VoteStatusText").objectReferenceValue = hudCanvas.transform.Find("Panel/StatusText")?.GetComponent<TMP_Text>();
            soHUD.ApplyModifiedProperties();

            Canvas.ForceUpdateCanvases();
            Selection.activeGameObject = rootGO;
            EditorUtility.DisplayDialog("Success", "Voting System Created!", "OK");
        }

        private void WireHostPanelReferences(SerializedObject so, GameObject hostPanel)
        {
            so.FindProperty("m_StartVoteButton").objectReferenceValue = hostPanel.transform.Find("BottomButtons/StartBtn")?.GetComponent<Button>();
            so.FindProperty("m_EndVoteButton").objectReferenceValue = hostPanel.transform.Find("BottomButtons/EndBtn")?.GetComponent<Button>();
            so.FindProperty("m_MultiChoiceTabButton").objectReferenceValue = hostPanel.transform.Find("TabButtons/MultiChoiceTab")?.GetComponent<Button>();
            so.FindProperty("m_HandRaiseTabButton").objectReferenceValue = hostPanel.transform.Find("TabButtons/HandRaiseTab")?.GetComponent<Button>();
            so.FindProperty("m_MultiChoiceTabContent").objectReferenceValue = hostPanel.transform.Find("TabContent/MultiChoiceContent")?.gameObject;
            so.FindProperty("m_HandRaiseTabContent").objectReferenceValue = hostPanel.transform.Find("TabContent/HandRaiseContent")?.gameObject;
            so.FindProperty("m_QuestionInput").objectReferenceValue = hostPanel.transform.Find("TabContent/MultiChoiceContent/QuestionInput")?.GetComponent<TMP_InputField>();
            so.FindProperty("m_YesNoButton").objectReferenceValue = hostPanel.transform.Find("TabContent/MultiChoiceContent/QuickPolls/YesNoBtn")?.GetComponent<Button>();
            so.FindProperty("m_AgreeDisagreeButton").objectReferenceValue = hostPanel.transform.Find("TabContent/MultiChoiceContent/QuickPolls/AgreeBtn")?.GetComponent<Button>();
            so.FindProperty("m_Rating1to5Button").objectReferenceValue = hostPanel.transform.Find("TabContent/MultiChoiceContent/QuickPolls/RatingBtn")?.GetComponent<Button>();
            
            // Add/Remove buttons
            so.FindProperty("m_AddOptionButton").objectReferenceValue = hostPanel.transform.Find("TabContent/MultiChoiceContent/AddRemoveRow/AddOptionBtn")?.GetComponent<Button>();
            so.FindProperty("m_RemoveOptionButton").objectReferenceValue = hostPanel.transform.Find("TabContent/MultiChoiceContent/AddRemoveRow/RemoveOptionBtn")?.GetComponent<Button>();
            so.FindProperty("m_OptionsContainer").objectReferenceValue = hostPanel.transform.Find("TabContent/MultiChoiceContent/Options");

            var optionInputsProp = so.FindProperty("m_OptionInputs");
            optionInputsProp.ClearArray();
            for (int i = 0; i < 6; i++)
            {
                var opt = hostPanel.transform.Find($"TabContent/MultiChoiceContent/Options/Option{i}")?.GetComponent<TMP_InputField>();
                if (opt != null)
                {
                    optionInputsProp.InsertArrayElementAtIndex(i);
                    optionInputsProp.GetArrayElementAtIndex(i).objectReferenceValue = opt;
                }
            }
        }

        private TMP_FontAsset LoadTMPFont()
        {
            string[] fontPaths = { "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset", "Packages/com.unity.textmeshpro/Editor Resources/Fonts & Materials/LiberationSans SDF.asset" };
            foreach (var path in fontPaths)
            {
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font != null) return font;
            }
            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
            if (guids.Length > 0) return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return null;
        }

        private GameObject CreateCanvas(string name, Transform parent, float scale, Vector2 size)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 50;
            go.AddComponent<GraphicRaycaster>();
            go.AddComponent<TrackedDeviceGraphicRaycaster>();

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * scale;
            go.layer = LayerMask.NameToLayer("UI");

            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x, size.y, 10);
            col.isTrigger = true;

            return go;
        }

        private GameObject CreateHostPanel(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            // Main panel - fills canvas
            GameObject panel = new GameObject("HostPanel");
            panel.transform.SetParent(parent, false);
            var rt = panel.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var panelImg = panel.AddComponent<Image>();
            panelImg.color = PanelBackground;
            if (roundedSprite != null) { panelImg.sprite = roundedSprite; panelImg.type = Image.Type.Sliced; panelImg.pixelsPerUnitMultiplier = 2f; }

            // Main vertical layout
            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(15, 15, 15, 15);
            vlg.spacing = 10;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // 1. TITLE
            CreateSimpleLabel(panel.transform, "Title", "CREATE VOTE", font, 20, FontStyles.Bold, 30);

            // 2. TAB BUTTONS ROW
            CreateTabButtonsRow(panel.transform, roundedSprite, font);

            // 3. TAB CONTENT CONTAINER
            CreateTabContentContainer(panel.transform, roundedSprite, font);

            // 4. BOTTOM BUTTONS
            CreateBottomButtons(panel.transform, roundedSprite, font);

            panel.SetActive(false);
            return panel;
        }

        private void CreateTabButtonsRow(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject row = new GameObject("TabButtons");
            row.transform.SetParent(parent, false);
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 40;
            le.minHeight = 40;
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;

            CreateTabButton(row.transform, "MultiChoiceTab", "Multi-Choice", true, roundedSprite, font);
            CreateTabButton(row.transform, "HandRaiseTab", "Hand Raise", false, roundedSprite, font);
        }

        private void CreateTabButton(Transform parent, string name, string label, bool active, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.color = active ? TabActive : TabInactive;
            if (roundedSprite != null) { img.sprite = roundedSprite; img.type = Image.Type.Sliced; }

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(180, 40, 5);

            // Text centered inside
            GameObject textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            var textRt = textGO.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            if (font != null) tmp.font = font;
        }

        private void CreateTabContentContainer(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject container = new GameObject("TabContent");
            container.transform.SetParent(parent, false);
            VotingUILayoutUtility.StretchToParent(container.AddComponent<RectTransform>());
            var le = container.AddComponent<LayoutElement>();
            le.flexibleHeight = 1;
            le.minHeight = 250;

            // Multi-Choice Content
            CreateMultiChoiceContent(container.transform, roundedSprite, font);

            // Hand Raise Content
            CreateHandRaiseContent(container.transform, roundedSprite, font);
        }

        private void CreateMultiChoiceContent(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject content = new GameObject("MultiChoiceContent");
            content.transform.SetParent(parent, false);
            var rt = content.AddComponent<RectTransform>();
            VotingUILayoutUtility.StretchToParent(rt);

            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 8;
            vlg.padding = new RectOffset(5, 5, 5, 5);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // Quick Polls label + buttons
            CreateSimpleLabel(content.transform, "QuickPollsLabel", "Quick Polls:", font, 12, FontStyles.Normal, 20);
            CreateQuickPollButtons(content.transform, roundedSprite, font);

            // Question input
            CreateSimpleLabel(content.transform, "QuestionLabel", "Question:", font, 12, FontStyles.Normal, 20);
            CreateInputField(content.transform, "QuestionInput", "Enter your question...", roundedSprite, font, 35);

            // Options
            CreateSimpleLabel(content.transform, "OptionsLabel", "Options:", font, 12, FontStyles.Normal, 20);
            CreateOptionsFields(content.transform, roundedSprite, font);

            content.SetActive(true);
        }

        private void CreateQuickPollButtons(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject row = new GameObject("QuickPolls");
            row.transform.SetParent(parent, false);
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 35;
            le.minHeight = 35;
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;

            CreateSmallButton(row.transform, "YesNoBtn", "Yes/No", roundedSprite, font);
            CreateSmallButton(row.transform, "AgreeBtn", "Agree/Disagree", roundedSprite, font);
            CreateSmallButton(row.transform, "RatingBtn", "1-5", roundedSprite, font);
        }

        private void CreateOptionsFields(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            // Add/Remove buttons row
            GameObject btnRow = new GameObject("AddRemoveRow");
            btnRow.transform.SetParent(parent, false);
            var rowLe = btnRow.AddComponent<LayoutElement>();
            rowLe.preferredHeight = 30;
            rowLe.minHeight = 30;
            var hlg = btnRow.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            
            CreateSmallButton(btnRow.transform, "RemoveOptionBtn", "− Remove", roundedSprite, font);
            CreateSmallButton(btnRow.transform, "AddOptionBtn", "+ Add Option", roundedSprite, font);
            
            // Options container
            GameObject container = new GameObject("Options");
            container.transform.SetParent(parent, false);
            VotingUILayoutUtility.StretchToParent(container.AddComponent<RectTransform>());
            var le = container.AddComponent<LayoutElement>();
            le.preferredHeight = 130;
            le.minHeight = 80;
            le.flexibleHeight = 1;
            var vlg = container.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;

            string[] defaults = { "Option A", "Option B", "Option C", "Option D", "Option E", "Option F" };
            for (int i = 0; i < 6; i++)
            {
                CreateInputField(container.transform, $"Option{i}", defaults[i], roundedSprite, font, 22);
            }
        }

        private void CreateHandRaiseContent(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject content = new GameObject("HandRaiseContent");
            content.transform.SetParent(parent, false);
            var rt = content.AddComponent<RectTransform>();
            VotingUILayoutUtility.StretchToParent(rt);

            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 15;
            vlg.padding = new RectOffset(10, 10, 20, 20);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;

            CreateSimpleLabel(content.transform, "Instruction", "Raise your hand to vote:", font, 16, FontStyles.Bold, 30);
            CreateSimpleLabel(content.transform, "LeftHand", "Left Hand = YES", font, 18, FontStyles.Normal, 35, StatusGreen);
            CreateSimpleLabel(content.transform, "RightHand", "Right Hand = NO", font, 18, FontStyles.Normal, 35, RecordingRed);

            content.SetActive(false);
        }

        private void CreateBottomButtons(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject row = new GameObject("BottomButtons");
            row.transform.SetParent(parent, false);
            VotingUILayoutUtility.StretchToParent(row.AddComponent<RectTransform>());
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 45;
            le.minHeight = 45;
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 15;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;

            CreateActionButton(row.transform, "StartBtn", "▶ START", ButtonAction, roundedSprite, font);
            CreateActionButton(row.transform, "EndBtn", "■ END", RecordingRed, roundedSprite, font);
        }

        private void CreateSimpleLabel(Transform parent, string name, string text, TMP_FontAsset font, int fontSize, FontStyles style, float height, Color? color = null)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color ?? Color.white;
            tmp.raycastTarget = false;
            if (font != null) tmp.font = font;
        }

        private void CreateSmallButton(Transform parent, string name, string label, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.color = ButtonNormal;
            if (roundedSprite != null) { img.sprite = roundedSprite; img.type = Image.Type.Sliced; }

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            go.AddComponent<BoxCollider>().size = new Vector3(100, 35, 5);

            GameObject textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            var textRt = textGO.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 11;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            if (font != null) tmp.font = font;
        }

        private void CreateActionButton(Transform parent, string name, string label, Color color, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.color = color;
            if (roundedSprite != null) { img.sprite = roundedSprite; img.type = Image.Type.Sliced; }

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            go.AddComponent<BoxCollider>().size = new Vector3(150, 45, 5);

            GameObject textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            var textRt = textGO.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 16;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            if (font != null) tmp.font = font;
        }

        private void CreateInputField(Transform parent, string name, string placeholder, Sprite roundedSprite, TMP_FontAsset font, float height)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;

            var img = go.AddComponent<Image>();
            img.color = InputFieldBg;
            if (roundedSprite != null) { img.sprite = roundedSprite; img.type = Image.Type.Sliced; }

            // Text Area
            GameObject textArea = new GameObject("Text Area");
            textArea.transform.SetParent(go.transform, false);
            var taRt = textArea.AddComponent<RectTransform>();
            taRt.anchorMin = Vector2.zero;
            taRt.anchorMax = Vector2.one;
            taRt.offsetMin = new Vector2(8, 2);
            taRt.offsetMax = new Vector2(-8, -2);

            // Text
            GameObject textGO = new GameObject("Text");
            textGO.transform.SetParent(textArea.transform, false);
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = "";
            tmp.fontSize = 12;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.color = Color.white;
            if (font != null) tmp.font = font;
            var textRt = textGO.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            // Placeholder
            GameObject phGO = new GameObject("Placeholder");
            phGO.transform.SetParent(textArea.transform, false);
            var phTmp = phGO.AddComponent<TextMeshProUGUI>();
            phTmp.text = placeholder;
            phTmp.fontSize = 12;
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.alignment = TextAlignmentOptions.MidlineLeft;
            phTmp.color = new Color(0.5f, 0.5f, 0.5f, 0.7f);
            if (font != null) phTmp.font = font;
            var phRt = phGO.GetComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = Vector2.zero;
            phRt.offsetMax = Vector2.zero;

            var input = go.AddComponent<TMP_InputField>();
            input.textComponent = tmp;
            input.placeholder = phTmp;
            input.textViewport = taRt;

            go.AddComponent<BoxCollider>().size = new Vector3(350, height, 5);
        }

        private GameObject CreateHUDCanvas(Transform parent, float scale, Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject go = CreateCanvas("HUDCanvas", parent, scale, new Vector2(300, 180));

            GameObject panel = new GameObject("Panel");
            panel.transform.SetParent(go.transform, false);
            var rt = panel.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var panelImg = panel.AddComponent<Image>();
            panelImg.color = new Color(0, 0, 0, 0.8f);
            if (roundedSprite != null) { panelImg.sprite = roundedSprite; panelImg.type = Image.Type.Sliced; }

            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(15, 15, 15, 15);
            vlg.spacing = 10;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;

            CreateSimpleLabel(panel.transform, "QuestionText", "Vote Question", font, 18, FontStyles.Bold, 30);

            GameObject optContainer = new GameObject("OptionsContainer");
            optContainer.transform.SetParent(panel.transform, false);
            var optLe = optContainer.AddComponent<LayoutElement>();
            optLe.preferredHeight = 80;
            optContainer.AddComponent<VerticalLayoutGroup>().spacing = 5;

            CreateSimpleLabel(panel.transform, "StatusText", "Point at a bar to vote!", font, 14, FontStyles.Italic, 25, StatusGreen);

            go.SetActive(false);
            return go;
        }
    }
}
#endif
