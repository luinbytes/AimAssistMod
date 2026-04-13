using System;
using UnityEngine.InputSystem;

namespace AimAssist
{
    internal static class InputHelper
    {
        // Returns true while the named key/button is held.
        public static bool IsHeld(string keyName)
        {
            // Mouse buttons handled separately
            switch (keyName)
            {
                case "Mouse0": return Mouse.current?.leftButton.isPressed   ?? false;
                case "Mouse1": return Mouse.current?.rightButton.isPressed  ?? false;
                case "Mouse2": return Mouse.current?.middleButton.isPressed ?? false;
                case "Mouse3": return Mouse.current?.backButton.isPressed   ?? false;
                case "Mouse4": return Mouse.current?.forwardButton.isPressed ?? false;
            }
            // Keyboard keys — parse to Key enum (case-insensitive)
            if (Enum.TryParse<Key>(keyName, ignoreCase: true, out var key) && Keyboard.current != null)
                return Keyboard.current[key].isPressed;
            return false;
        }

        // Returns true on the first frame the key/button is pressed.
        public static bool WasPressedThisFrame(string keyName)
        {
            switch (keyName)
            {
                case "Mouse0": return Mouse.current?.leftButton.wasPressedThisFrame   ?? false;
                case "Mouse1": return Mouse.current?.rightButton.wasPressedThisFrame  ?? false;
                case "Mouse2": return Mouse.current?.middleButton.wasPressedThisFrame ?? false;
                case "Mouse3": return Mouse.current?.backButton.wasPressedThisFrame   ?? false;
                case "Mouse4": return Mouse.current?.forwardButton.wasPressedThisFrame ?? false;
            }
            if (Enum.TryParse<Key>(keyName, ignoreCase: true, out var key) && Keyboard.current != null)
                return Keyboard.current[key].wasPressedThisFrame;
            return false;
        }
    }
}
