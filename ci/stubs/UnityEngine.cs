// Minimal compile-time stubs for UnityEngine types used by AimAssist.
// These have empty bodies — only the public API surface matters for the C# compiler.
// DO NOT use these at runtime; they exist solely so CI can build without game DLLs.

using System;
using System.Collections;

#pragma warning disable CS8618, CS0649, CS0067

namespace UnityEngine
{
    // ── Attributes ─────────────────────────────────────────────────────────────
    [AttributeUsage(AttributeTargets.Class)] public sealed class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int order) { } }
    [AttributeUsage(AttributeTargets.Class)] public sealed class RequireComponent  : Attribute { public RequireComponent(Type t) { } }

    // ── Primitives ──────────────────────────────────────────────────────────────
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } public static Vector2 zero => default; }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float magnitude => 0f;
        public float sqrMagnitude => 0f;
        public Vector3 normalized => default;
        public static Vector3 zero    => default;
        public static Vector3 up      => new Vector3(0,1,0);
        public static Vector3 forward => new Vector3(0,0,1);
        public static float   Distance(Vector3 a, Vector3 b) => 0f;
        public static Vector3 operator +(Vector3 a, Vector3 b) => default;
        public static Vector3 operator -(Vector3 a, Vector3 b) => default;
        public static Vector3 operator *(Vector3 a, float b)   => default;
        public static Vector3 operator *(float a,   Vector3 b) => default;
        public static bool    operator ==(Vector3 a, Vector3 b) => false;
        public static bool    operator !=(Vector3 a, Vector3 b) => true;
        public override bool  Equals(object? obj) => false;
        public override int   GetHashCode() => 0;
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity => default;
        public static Quaternion Euler(float x, float y, float z) => default;
        public Vector3 eulerAngles { get => default; set { } }
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t) => default;
        public static Quaternion LookRotation(Vector3 forward) => default;
        public static Quaternion AngleAxis(float angle, Vector3 axis) => default;
        public static Quaternion operator *(Quaternion a, Quaternion b) => default;
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r=r; this.g=g; this.b=b; this.a=a; }
        public static Color white   => new Color(1,1,1);
        public static Color black   => new Color(0,0,0);
        public static Color red     => new Color(1,0,0);
        public static Color green   => new Color(0,1,0);
        public static Color blue    => new Color(0,0,1);
        public static Color yellow  => new Color(1,1,0);
        public static Color clear   => new Color(0,0,0,0);
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x=x; this.y=y; this.width=width; this.height=height; }
    }

    public struct RaycastHit { public Vector3 point; public Vector3 normal; }

    // ── Math / Physics ─────────────────────────────────────────────────────────
    public static class Mathf
    {
        public const float Deg2Rad = 0.0174533f;
        public const float Rad2Deg = 57.29578f;
        public const float PI      = 3.14159274f;
        public const float Infinity         = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public static float Sqrt(float f)  => 0f;
        public static float Abs(float f)   => 0f;
        public static float Sin(float f)   => 0f;
        public static float Cos(float f)   => 0f;
        public static float Tan(float f)   => 0f;
        public static float Atan(float f)  => 0f;
        public static float Atan2(float y, float x) => 0f;
        public static float Clamp(float v, float min, float max) => 0f;
        public static float Clamp01(float v) => 0f;
        public static float Lerp(float a, float b, float t) => 0f;
        public static float InverseLerp(float a, float b, float v) => 0f;
        public static float Max(float a, float b) => 0f;
        public static float Max(float a, float b, float c) => 0f;
        public static float Min(float a, float b) => 0f;
        public static float MoveTowards(float cur, float tgt, float maxDelta) => 0f;
        public static float MoveTowardsAngle(float cur, float tgt, float maxDelta) => 0f;
        public static float DeltaAngle(float cur, float tgt) => 0f;
        public static float LerpAngle(float a, float b, float t) => 0f;
        public static bool  Approximately(float a, float b) => false;
        public static float Sign(float f) => 0f;
        public static float Round(float f) => 0f;
        public static float Ceil(float f) => 0f;
        public static float Floor(float f) => 0f;
        public static int   CeilToInt(float f) => 0;
        public static int   FloorToInt(float f) => 0;
        public static int   RoundToInt(float f) => 0;
    }

    public static class Physics
    {
        public static Vector3 gravity => default;
        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hitInfo, float maxDistance = float.MaxValue)
        {
            hitInfo = default;
            return false;
        }
    }

    public static class Time
    {
        public static float deltaTime      => 0f;
        public static float time           => 0f;
        public static float fixedDeltaTime => 0f;
        public static float unscaledDeltaTime => 0f;
    }

    public static class Screen
    {
        public static int   width  => 0;
        public static int   height => 0;
        public static float dpi    => 0f;
    }

    // ── Object hierarchy ────────────────────────────────────────────────────────
    public class Object
    {
        public string name { get; set; } = "";
        public static implicit operator bool(Object? o) => o != null;
        public static T? FindObjectOfType<T>() where T : Object => null;
        public static T[]? FindObjectsOfType<T>() where T : Object => null;
        public static Object? FindObjectOfType(Type t) => null;
        public static Object[]? FindObjectsOfType(Type t) => null;
        public static void DontDestroyOnLoad(Object o) { }
        public static void Destroy(Object o) { }
    }

    public class Component : Object
    {
        public Transform transform { get; } = null!;
        public GameObject gameObject { get; } = null!;
        public T? GetComponent<T>() where T : Component => null;
        public Component? GetComponent(Type t) => null;
        public T[] GetComponents<T>() where T : Component => Array.Empty<T>();
        public T[] GetComponentsInChildren<T>() where T : Component => Array.Empty<T>();
    }

    public class Transform : Component
    {
        public Vector3    position      { get; set; }
        public Vector3    localPosition { get; set; }
        public Vector3    eulerAngles   { get; set; }
        public Vector3    localEulerAngles { get; set; }
        public Quaternion rotation      { get; set; }
        public Quaternion localRotation { get; set; }
        public Transform? parent        { get; set; }
        public Transform? root          => null;
        public int        childCount    => 0;
        public Transform? GetChild(int i) => null;
        public void SetParent(Transform? p) { }
        public new MonoBehaviour[] GetComponents<MonoBehaviour>() => Array.Empty<MonoBehaviour>();
    }

    public class GameObject : Object
    {
        public GameObject(string name) { this.name = name; }
        public Transform transform { get; } = null!;
        public bool      activeSelf => true;
        public void      SetActive(bool v) { }
        public T         AddComponent<T>() where T : Component => null!;
        public T?        GetComponent<T>() where T : Component => null;
    }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => null!;
        public Coroutine StartCoroutine(string methodName)   => null!;
        public void      StopCoroutine(Coroutine c) { }
        public void      StopAllCoroutines() { }
        public bool      enabled { get; set; }
    }

    public class Behaviour    : Component { }
    public class Coroutine    : YieldInstruction { }
    public class YieldInstruction { }
    public class WaitForSeconds : YieldInstruction { public WaitForSeconds(float seconds) { } }

    public class Camera : Behaviour
    {
        public static Camera? main { get; }
        public static event Action<Camera>? onPreRender;
        public static event Action<Camera>? onPostRender;
        public bool          orthographic    { get; set; }
        public float         fieldOfView     { get; set; }
        public float         nearClipPlane   { get; set; }
        public float         farClipPlane    { get; set; }
        public Vector3       forward         => default;
    }

    // ── IMGUI ───────────────────────────────────────────────────────────────────
    public enum FontStyle  { Normal, Bold, Italic, BoldAndItalic }
    public enum EventType  { Repaint, Layout, MouseDown, MouseUp, KeyDown, KeyUp, ScrollWheel, Used, Ignore }
    public enum TextAnchor { UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight }

    public class GUIStyleState { public Color textColor { get; set; } public Texture2D? background { get; set; } }

    public class GUIStyle
    {
        public GUIStyle() { }
        public GUIStyle(GUIStyle other) { }
        public int        fontSize   { get; set; }
        public FontStyle  fontStyle  { get; set; }
        public bool       wordWrap   { get; set; }
        public TextAnchor alignment  { get; set; }
        public RectOffset padding    { get; set; } = new RectOffset();
        public RectOffset margin     { get; set; } = new RectOffset();
        public GUIStyleState normal  { get; set; } = new GUIStyleState();
        public GUIStyleState hover   { get; set; } = new GUIStyleState();
        public GUIStyleState active  { get; set; } = new GUIStyleState();
        public Vector2 CalcSize(GUIContent content) => default;
    }

    public class RectOffset
    {
        public int left, right, top, bottom;
        public RectOffset() { }
        public RectOffset(int left, int right, int top, int bottom) { this.left=left; this.right=right; this.top=top; this.bottom=bottom; }
    }

    public class GUIContent
    {
        public static GUIContent none { get; } = new GUIContent();
        public GUIContent() { }
        public GUIContent(string text) { }
    }

    public class GUISkin
    {
        public GUIStyle label  { get; } = new GUIStyle();
        public GUIStyle box    { get; } = new GUIStyle();
        public GUIStyle button { get; } = new GUIStyle();
    }

    public class Texture2D : Object { }

    public static class GUI
    {
        public static Color   color   { get; set; }
        public static GUISkin skin    { get; } = new GUISkin();
        public static void    Box(Rect position, GUIContent content, GUIStyle style) { }
        public static void    Box(Rect position, string text) { }
        public static void    Label(Rect position, string text, GUIStyle style) { }
        public static void    Label(Rect position, GUIContent content, GUIStyle style) { }
        public static void    DrawTexture(Rect position, Texture2D image) { }
        public static void    BeginGroup(Rect position) { }
        public static void    EndGroup() { }
    }

    public class Event
    {
        public static Event?  current { get; }
        public EventType      type    { get; }
    }
}
