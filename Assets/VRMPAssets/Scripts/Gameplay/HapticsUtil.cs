using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace XRMultiplayer
{
    /// <summary>
    /// Static utility for sending haptic impulses to XR controllers.
    /// Uses Unity's <see cref="InputDevice"/> API (OpenXR / XRI compatible).
    /// </summary>
    public static class HapticsUtil
    {
        // Reusable list to avoid allocations every call.
        static readonly List<InputDevice> s_Devices = new List<InputDevice>(2);

        /// <summary>
        /// Sends a haptic impulse to the controller matching <paramref name="hand"/>.
        /// </summary>
        /// <param name="hand">Which hand's controller to vibrate.</param>
        /// <param name="amplitude">Vibration intensity, 0 (off) to 1 (max).</param>
        /// <param name="duration">Vibration length in seconds.</param>
        public static void SendHapticImpulse(HighFiveSnap.HandSide hand, float amplitude, float duration)
        {
            InputDeviceCharacteristics characteristics =
                InputDeviceCharacteristics.Controller |
                InputDeviceCharacteristics.HeldInHand;

            characteristics |= hand == HighFiveSnap.HandSide.Left
                ? InputDeviceCharacteristics.Left
                : InputDeviceCharacteristics.Right;

            s_Devices.Clear();
            InputDevices.GetDevicesWithCharacteristics(characteristics, s_Devices);

            foreach (InputDevice device in s_Devices)
            {
                if (device.TryGetHapticCapabilities(out HapticCapabilities caps) && caps.supportsImpulse)
                {
                    device.SendHapticImpulse(0, Mathf.Clamp01(amplitude), duration);
                }
            }
        }
    }
}
