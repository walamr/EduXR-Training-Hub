#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using Unity.Netcode;
using TMPro;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace XRMultiplayer
{
    /// <summary>
    /// Editor wizard to setup the Podium Mode system in the scene.
    /// Creates a PodiumMode_Root with PodiumManager + PodiumModeUI.
    /// The "Podium" button is added to the Quick Menu dynamically at runtime (host-only).
    /// No separate panel is needed.
    /// </summary>
    public class PodiumModeSetup : EditorWindow
    {
        private static readonly Color PanelBackground = new Color(0.106f, 0.106f, 0.106f, 0.95f);
        private static readonly Color ButtonSuccess = new Color(0.13f, 0.8f, 0.4f, 1f);
        private static readonly Color HandYellow = new Color(1f, 0.85f, 0.2f, 1f);
        private static readonly string ROUNDED_SPRITE_PATH = "Assets/VRMPAssets/Textures/UI/Round Radius 10.png";

        private float canvasScale = 0.001f;

        [MenuItem("VRMP/Setup Podium Mode")]
        public static void ShowWindow()
        {
            GetWindow<PodiumModeSetup>("Podium Mode Setup");
        }

        private void OnGUI()
        {
            GUILayout.Label("Podium Mode Setup Wizard", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Creates the Podium Mode system:\n" +
                "- PodiumManager (NetworkBehaviour)\n" +
                "- PodiumModeUI (auto-registers Quick Menu buttons at runtime)\n" +
                "- Podium spot marker (move it to where the host stands)\n" +
                "- Raised-hands HUD (floating list for the host)\n\n" +
                "The 'Podium' button appears in the Quick Menu for the HOST ONLY at runtime.\n" +
                "Non-host players get a 'Raise Hand' button when Podium Mode is active.\n" +
                "No separate panel needed - one click toggles the mode.",
                MessageType.Info);
            EditorGUILayout.Space();

            canvasScale = EditorGUILayout.FloatField("HUD Canvas Scale", canvasScale);
            EditorGUILayout.HelpBox("0.001 = standard VR scale (1 pixel = 1mm)", MessageType.None);

            EditorGUILayout.Space(20);

            if (GUILayout.Button("Setup Podium Mode", GUILayout.Height(40)))
            {
                SetupPodiumMode();
            }
        }

        private void SetupPodiumMode()
        {
            Sprite roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(ROUNDED_SPRITE_PATH);
            TMP_FontAsset tmpFont = LoadTMPFont();

            // 1. Root GameObject
            GameObject rootGO = new GameObject("PodiumMode_Root");
            Undo.RegisterCreatedObjectUndo(rootGO, "Create Podium Mode");
            rootGO.AddComponent<NetworkObject>();
            var podiumManager = rootGO.AddComponent<PodiumManager>();

            // 2. Podium Spot marker
            GameObject podiumSpot = new GameObject("PodiumSpot");
            podiumSpot.transform.SetParent(rootGO.transform);
            podiumSpot.transform.localPosition = new Vector3(0, 0, 2f);

            // 3. Raised Hands HUD (floating world-space canvas for the host)
            GameObject hudCanvas = CreateRaisedHandsHUD(rootGO.transform, roundedSprite, tmpFont);

            // 4. Raised Hand Entry Prefab
            GameObject entryPrefab = CreateRaisedHandEntryPrefab(roundedSprite, tmpFont);

            // 5. PodiumModeUI
            var podiumUI = rootGO.AddComponent<PodiumModeUI>();

            // 6. Wire references
            SerializedObject soManager = new SerializedObject(podiumManager);
            soManager.FindProperty("m_PodiumSpot").objectReferenceValue = podiumSpot.transform;
            soManager.ApplyModifiedProperties();

            SerializedObject soUI = new SerializedObject(podiumUI);
            soUI.FindProperty("m_RaisedHandsHUD").objectReferenceValue = hudCanvas;
            soUI.FindProperty("m_RaisedHandsListContainer").objectReferenceValue =
                hudCanvas.transform.Find("Panel/HandsList");
            soUI.FindProperty("m_RaisedHandEntryPrefab").objectReferenceValue = entryPrefab;
            soUI.ApplyModifiedProperties();

            // Done
            Canvas.ForceUpdateCanvases();
            Selection.activeGameObject = rootGO;

            Debug.Log("[PodiumModeSetup] Setup complete!");
            EditorUtility.DisplayDialog("Podium Mode Setup",
                "Podium Mode system created!\n\n" +
                "Next steps:\n" +
                "1. Move 'PodiumSpot' to where the host should stand\n" +
                "2. Chairs (SittableChair) are auto-discovered at runtime\n" +
                "3. 'Podium' button auto-appears in Quick Menu for host only\n" +
                "4. 'Raise Hand' button auto-appears for non-host during Podium Mode",
                "OK");
        }

        #region UI Builders

        private GameObject CreateRaisedHandsHUD(Transform parent, Sprite roundedSprite, TMP_FontAsset font)
        {
            // World-space canvas
            GameObject canvasGO = new GameObject("RaisedHandsHUD");
            canvasGO.transform.SetParent(parent, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 70;
            canvasGO.AddComponent<GraphicRaycaster>();
            canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();

            var rt = canvasGO.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(250, 300);
            rt.localScale = Vector3.one * canvasScale;
            canvasGO.layer = LayerMask.NameToLayer("UI");

            var col = canvasGO.AddComponent<BoxCollider>();
            col.size = new Vector3(250, 300, 10);
            col.isTrigger = true;

            // Panel background
            GameObject panel = new GameObject("Panel");
            panel.transform.SetParent(canvasGO.transform, false);
            var panelRt = panel.AddComponent<RectTransform>();
            panelRt.anchorMin = Vector2.zero;
            panelRt.anchorMax = Vector2.one;
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            var panelImg = panel.AddComponent<Image>();
            panelImg.color = PanelBackground;
            if (roundedSprite != null) { panelImg.sprite = roundedSprite; panelImg.type = Image.Type.Sliced; panelImg.pixelsPerUnitMultiplier = 2f; }

            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 10, 10);
            vlg.spacing = 5;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // Title
            CreateLabel(panel.transform, "Title", "\u270B Raised Hands", font, 14, FontStyles.Bold, 25, HandYellow);

            // List container
            GameObject list = new GameObject("HandsList");
            list.transform.SetParent(panel.transform, false);
            var listLe = list.AddComponent<LayoutElement>();
            listLe.flexibleHeight = 1;
            listLe.minHeight = 150;
            var listVlg = list.AddComponent<VerticalLayoutGroup>();
            listVlg.spacing = 4;
            listVlg.childControlWidth = true;
            listVlg.childControlHeight = true;
            listVlg.childForceExpandWidth = true;
            listVlg.childForceExpandHeight = false;

            // Placeholder
            CreateLabel(list.transform, "EmptyText", "No hands raised yet...", font, 11, FontStyles.Italic, 25, new Color(0.5f, 0.5f, 0.5f));

            canvasGO.SetActive(false); // Hidden by default
            return canvasGO;
        }

        private GameObject CreateRaisedHandEntryPrefab(Sprite roundedSprite, TMP_FontAsset font)
        {
            GameObject prefab = new GameObject("RaisedHandEntry");
            var rt = prefab.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(220, 35);

            var img = prefab.AddComponent<Image>();
            img.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);
            if (roundedSprite != null) { img.sprite = roundedSprite; img.type = Image.Type.Sliced; }

            var hlg = prefab.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(8, 8, 4, 4);
            hlg.spacing = 8;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;

            var prefabLe = prefab.AddComponent<LayoutElement>();
            prefabLe.preferredHeight = 35;
            prefabLe.minHeight = 35;

            // Player name
            GameObject nameGO = new GameObject("PlayerName");
            nameGO.transform.SetParent(prefab.transform, false);
            var nameLe = nameGO.AddComponent<LayoutElement>();
            nameLe.flexibleWidth = 1;
            var nameTmp = nameGO.AddComponent<TextMeshProUGUI>();
            nameTmp.text = "\u270B Player";
            nameTmp.fontSize = 12;
            nameTmp.alignment = TextAlignmentOptions.MidlineLeft;
            nameTmp.color = HandYellow;
            nameTmp.raycastTarget = false;
            if (font != null) nameTmp.font = font;

            // Unmute button
            GameObject btnGO = new GameObject("UnmuteBtn");
            btnGO.transform.SetParent(prefab.transform, false);
            var btnLe = btnGO.AddComponent<LayoutElement>();
            btnLe.preferredWidth = 70;
            var btnImg = btnGO.AddComponent<Image>();
            btnImg.color = ButtonSuccess;
            if (roundedSprite != null) { btnImg.sprite = roundedSprite; btnImg.type = Image.Type.Sliced; }
            btnGO.AddComponent<Button>().targetGraphic = btnImg;
            btnGO.AddComponent<BoxCollider>().size = new Vector3(70, 35, 5);

            GameObject btnText = new GameObject("Text");
            btnText.transform.SetParent(btnGO.transform, false);
            var btnTextRt = btnText.AddComponent<RectTransform>();
            btnTextRt.anchorMin = Vector2.zero;
            btnTextRt.anchorMax = Vector2.one;
            btnTextRt.offsetMin = Vector2.zero;
            btnTextRt.offsetMax = Vector2.zero;
            var btnTmp = btnText.AddComponent<TextMeshProUGUI>();
            btnTmp.text = "Unmute";
            btnTmp.fontSize = 10;
            btnTmp.fontStyle = FontStyles.Bold;
            btnTmp.alignment = TextAlignmentOptions.Center;
            btnTmp.color = Color.white;
            if (font != null) btnTmp.font = font;

            // Save as prefab
            string prefabPath = "Assets/VRMPAssets/Prefabs/UI/RaisedHandEntry.prefab";
            SavePrefab(prefab, prefabPath);
            var savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            DestroyImmediate(prefab);
            return savedPrefab;
        }

        #endregion

        #region Helpers

        private void CreateLabel(Transform parent, string name, string text, TMP_FontAsset font,
            int fontSize, FontStyles style, float height, Color? color = null)
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

        private TMP_FontAsset LoadTMPFont()
        {
            string[] fontPaths = {
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset",
                "Packages/com.unity.textmeshpro/Editor Resources/Fonts & Materials/LiberationSans SDF.asset"
            };
            foreach (var path in fontPaths)
            {
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font != null) return font;
            }
            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
            if (guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return null;
        }

        private void SavePrefab(GameObject go, string path)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }
            if (!AssetDatabase.LoadAssetAtPath<GameObject>(path))
            {
                PrefabUtility.SaveAsPrefabAsset(go, path);
            }
        }

        #endregion
    }
}
#endif
