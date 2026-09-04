#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Unity.Netcode;
using Unity.Netcode.Components;

namespace XRMultiplayer.ChairEditor
{
    public class ChairSetupEditor : UnityEditor.Editor
    {
        [MenuItem("VRMP/Creation/Setup Selected Chair (Auto)")]
        public static void SetupSelectedChair()
        {
            GameObject obj = Selection.activeGameObject;
            if (obj == null)
            {
                Debug.LogWarning("Select a Chair object first!");
                return;
            }

            Undo.RecordObject(obj, "Setup Chair");

            // 1. NetworkObject
            if (!obj.TryGetComponent<NetworkObject>(out var netObj))
                Undo.AddComponent<NetworkObject>(obj);

            // 2. Rigidbody
            if (!obj.TryGetComponent<Rigidbody>(out var rb))
            {
                rb = Undo.AddComponent<Rigidbody>(obj);
                rb.mass = 20f;
                rb.linearDamping = 1f;
                rb.angularDamping = 1f;
                rb.isKinematic = false;
                rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            }

            // 3. NetworkTransform
            if (!obj.TryGetComponent<NetworkTransform>(out var netTransform))
            {
                netTransform = Undo.AddComponent<NetworkTransform>(obj);
                netTransform.InLocalSpace = true; // Use local space for flexibility
                netTransform.Interpolate = true;
                netTransform.SyncPositionX = true;
                netTransform.SyncPositionY = true;
                netTransform.SyncPositionZ = true;
                netTransform.SyncScaleX = false;
                netTransform.SyncScaleY = false;
                netTransform.SyncScaleZ = false;
            }

            // 4. XRSimpleInteractable
            if (!obj.TryGetComponent<XRSimpleInteractable>(out var interactable))
                Undo.AddComponent<XRSimpleInteractable>(obj);

            // 5. Collider Check
            if (obj.GetComponent<Collider>() == null)
            {
                var box = Undo.AddComponent<BoxCollider>(obj);
                box.size = Vector3.one;
                box.center = Vector3.up * 0.5f;
            }

            // 6. Scripts
            SittableChair chairScript = obj.GetComponent<SittableChair>();
            if (chairScript == null)
                chairScript = Undo.AddComponent<SittableChair>(obj);
            
            // 6b. Seat Point (New Request)
            // Use SerializedObject to access private 'seatPoint' field
            SerializedObject serializedChair = new SerializedObject(chairScript);
            SerializedProperty seatPointProp = serializedChair.FindProperty("seatPoint");

            if (seatPointProp != null && seatPointProp.objectReferenceValue == null)
            {
                // Check if a child named "SeatPoint" already exists
                Transform existingSeat = obj.transform.Find("SeatPoint");
                if (existingSeat == null)
                {
                    GameObject seatObj = new GameObject("SeatPoint");
                    seatObj.transform.SetParent(obj.transform, false);
                    // Default to slightly forward/up if needed, or 0,0,0
                    seatObj.transform.localPosition = new Vector3(0, 0.5f, 0); 
                    Undo.RegisterCreatedObjectUndo(seatObj, "Create SeatPoint");
                    existingSeat = seatObj.transform;
                    Debug.Log("Created new 'SeatPoint' child object.");
                }
                
                seatPointProp.objectReferenceValue = existingSeat;
                serializedChair.ApplyModifiedProperties();
            }
            
            if (obj.GetComponent<ChairLocomotion>() == null)
                Undo.AddComponent<ChairLocomotion>(obj);

            Debug.Log($"Success! '{obj.name}' is now a networked, physics chair.");
        }

        [MenuItem("VRMP/Creation/Create Chair Manager")]
        public static void CreateChairManager()
        {
            if (FindFirstObjectByType<ChairManager>() != null)
            {
                Debug.Log("ChairManager already exists.");
                return;
            }

            GameObject go = new GameObject("ChairManager");
            Undo.RegisterCreatedObjectUndo(go, "Create ChairManager");
            go.AddComponent<ChairManager>();
            Selection.activeGameObject = go;
            Debug.Log("Created ChairManager.");
        }
    }
}
#endif
