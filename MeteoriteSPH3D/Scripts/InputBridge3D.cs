using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MeteoriteSPH3D
{
    public static class InputBridge3D
    {
        public static bool KeyDown(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return false;

            if (key == KeyCode.R) return keyboard.rKey.wasPressedThisFrame;
            if (key == KeyCode.S) return keyboard.sKey.wasPressedThisFrame;
            if (key == KeyCode.Space) return keyboard.spaceKey.wasPressedThisFrame;
            if (key == KeyCode.M) return keyboard.mKey.wasPressedThisFrame;
            if (key == KeyCode.F1) return keyboard.f1Key.wasPressedThisFrame;
            if (key == KeyCode.LeftShift || key == KeyCode.RightShift)
                return keyboard.leftShiftKey.wasPressedThisFrame || keyboard.rightShiftKey.wasPressedThisFrame;
            return false;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(key);
#else
            return false;
#endif
        }

        public static bool Key(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return false;

            if (key == KeyCode.LeftShift || key == KeyCode.RightShift)
                return keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            return false;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(key);
#else
            return false;
#endif
        }

        public static bool MouseDown(int button)
        {
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return false;

            if (button == 0) return mouse.leftButton.wasPressedThisFrame;
            if (button == 1) return mouse.rightButton.wasPressedThisFrame;
            if (button == 2) return mouse.middleButton.wasPressedThisFrame;
            return false;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonDown(button);
#else
            return false;
#endif
        }

        public static bool Mouse(int button)
        {
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return false;

            if (button == 0) return mouse.leftButton.isPressed;
            if (button == 1) return mouse.rightButton.isPressed;
            if (button == 2) return mouse.middleButton.isPressed;
            return false;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButton(button);
#else
            return false;
#endif
        }

        public static Vector2 MousePosition()
        {
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null ? mouse.position.ReadValue() : Vector2.zero;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.mousePosition;
#else
            return Vector2.zero;
#endif
        }

        public static Vector2 MouseDelta()
        {
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null ? mouse.delta.ReadValue() : Vector2.zero;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#else
            return Vector2.zero;
#endif
        }

        public static float ScrollY()
        {
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null ? mouse.scroll.ReadValue().y * 0.02f : 0f;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.mouseScrollDelta.y;
#else
            return 0f;
#endif
        }
    }
}
