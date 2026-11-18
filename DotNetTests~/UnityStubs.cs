using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name { get; set; } = string.Empty;
        public static T FindFirstObjectByType<T>() where T : Object => null;
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() => new T();
    }

    public class Component : Object
    {
        public GameObject gameObject { get; set; }
    }

    public class MonoBehaviour : Component { }

    public sealed class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string name) { this.name = name; }
        public T AddComponent<T>() where T : Component, new() => new T { gameObject = this };
    }

    public sealed class RenderTexture : Object
    {
        public int width { get; set; }
        public int height { get; set; }
        public int antiAliasing { get; set; } = 1;
        public Experimental.Rendering.GraphicsFormat graphicsFormat { get; set; }
        public Experimental.Rendering.GraphicsFormat depthStencilFormat { get; set; }
        public bool bindTextureMS { get; set; }
        public bool useMipMap { get; set; }
        public bool autoGenerateMips { get; set; }
    }

    public static class Resources
    {
        public static T[] FindObjectsOfTypeAll<T>() where T : Object => Array.Empty<T>();
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class AddComponentMenu : Attribute
    {
        public AddComponentMenu(string menuName) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HeaderAttribute : Attribute
    {
        public HeaderAttribute(string header) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
        public static Color white => new Color(1f, 1f, 1f, 1f);
        public static Color black => new Color(0f, 0f, 0f, 1f);
    }

    public static class Mathf
    {
        public static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);
        public static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f);
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
    }

    public static class Time
    {
        public static int frameCount { get; set; }
        public static float unscaledDeltaTime { get; set; } = 0.0166f;
        public static float unscaledTime { get; set; }
    }

    public static class Application
    {
        public static bool isPlaying => false;
    }

    public static class Debug
    {
        public static void Log(object message) { }
    }

    public struct FrameTiming
    {
        public double cpuMainThreadFrameTime;
        public double cpuRenderThreadFrameTime;
        public double gpuFrameTime;
    }

    public static class FrameTimingManager
    {
        public static void CaptureFrameTimings() { }
        public static uint GetLatestTimings(uint numFrames, FrameTiming[] timings) => 0;
    }

    public sealed class GUIContent
    {
        public static readonly GUIContent none = new GUIContent();
    }

    public sealed class GUIStyleState
    {
        public Color textColor { get; set; }
    }

    public sealed class GUIStyle
    {
        public bool richText { get; set; }
        public int fontSize { get; set; }
        public GUIStyleState normal { get; } = new GUIStyleState();
        public GUIStyle() { }
        public GUIStyle(GUIStyle other) { }
    }

    public sealed class GUISkin
    {
        public GUIStyle box { get; set; } = new GUIStyle();
        public GUIStyle label { get; set; } = new GUIStyle();
    }

    public static class GUI
    {
        public static GUISkin skin { get; } = new GUISkin();
        public static void Box(Rect position, GUIContent content, GUIStyle style) { }
    }

    public sealed class GUILayoutOption { }

    public static class GUILayout
    {
        public static void BeginArea(Rect screenRect) { }
        public static void EndArea() { }
        public static void Label(string text, GUIStyle style) { }
        public static bool Button(string text, params GUILayoutOption[] options) => false;
        public static GUILayoutOption Height(float h) => new GUILayoutOption();
    }
}

namespace UnityEngine.Profiling
{
    public static class Profiler
    {
        public static long GetTotalAllocatedMemoryLong() => 0;
        public static long GetMonoUsedSizeLong() => 0;
    }
}

namespace UnityEngine.Experimental.Rendering
{
    public enum GraphicsFormat
    {
        None = 0,
        R8_SRGB = 1,
        R8_UNorm = 5,
        R8G8_UNorm = 6,
        R8G8B8A8_SRGB = 4,
        R8G8B8A8_UNorm = 8,
        B8G8R8A8_SRGB = 36,
        B8G8R8A8_UNorm = 37,
        R16_SFloat = 45,
        R16G16_SFloat = 46,
        R16G16B16A16_UNorm = 20,
        R16G16B16A16_SFloat = 48,
        R32_SFloat = 49,
        R32G32_SFloat = 50,
        R32G32B32A32_SFloat = 52,
        B10G11R11_UFloatPack32 = 59,
        A2B10G10R10_UNormPack32 = 54,
        D16_UNorm = 90,
        D24_UNorm = 91,
        D24_UNorm_S8_UInt = 92,
        D32_SFloat = 93,
        D32_SFloat_S8_UInt = 94
    }
}

namespace UnityEngine.Rendering { }

namespace UnityEditor
{
    public enum MessageType { None, Info, Warning, Error }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName) { }
    }

    public class EditorWindow : UnityEngine.ScriptableObject
    {
        public UnityEngine.Vector2 minSize { get; set; }
        public UnityEngine.UIElements.VisualElement rootVisualElement { get; } = new UnityEngine.UIElements.VisualElement();
        public static T GetWindow<T>(string title) where T : EditorWindow, new() => new T();
        public void Show() { }
        public void Repaint() { }
    }

    public static class EditorStyles
    {
        public static UnityEngine.GUIStyle boldLabel { get; } = new UnityEngine.GUIStyle();
        public static UnityEngine.GUIStyle wordWrappedMiniLabel { get; } = new UnityEngine.GUIStyle();
        public static UnityEngine.GUIStyle helpBox { get; } = new UnityEngine.GUIStyle();
    }

    public static class EditorGUILayout
    {
        public static void Space(float width = 6f) { }
        public static void LabelField(string label, params object[] args) { }
        public static void HelpBox(string message, MessageType type) { }
        public static void BeginHorizontal() { }
        public static void EndHorizontal() { }
        public static void BeginVertical(params object[] args) { }
        public static void EndVertical() { }
        public static UnityEngine.Vector2 BeginScrollView(UnityEngine.Vector2 pos) => pos;
        public static void EndScrollView() { }
        public static float Slider(string label, float value, float left, float right) => value;
        public static int IntField(string label, int value) => value;
        public static Enum EnumPopup(string label, Enum selected) => selected;
        public static bool ToggleLeft(string label, bool value) => value;
    }

    public static class EditorUtility
    {
        public static bool DisplayDialog(string title, string message, string ok) => true;
        public static string SaveFilePanel(string title, string directory, string defaultName, string extension) => string.Empty;
        public static void RevealInFinder(string path) { }
    }

    public static class Selection
    {
        public static UnityEngine.GameObject activeGameObject { get; set; }
    }

    public static class Undo
    {
        public static void RegisterCreatedObjectUndo(UnityEngine.Object obj, string name) { }
    }
}

namespace UnityEngine.UIElements
{
    public enum FlexDirection { Column, ColumnReverse, Row, RowReverse }
    public enum Align { Auto, FlexStart, Center, FlexEnd, Stretch }
    public enum Justify { FlexStart, Center, FlexEnd, SpaceBetween, SpaceAround }
    public enum Wrap { NoWrap, Wrap, WrapReverse }
    public enum WhiteSpace { Normal, NoWrap }
    public enum HelpBoxMessageType { None, Info, Warning, Error }

    public struct Length
    {
        public float value { get; set; }
        public Length(float value) { this.value = value; }
        public static Length Percent(float value) => new Length(value);
        public static implicit operator Length(float v) => new Length(v);
    }

    public struct StyleLength
    {
        public Length value { get; set; }
        public StyleLength(Length v) { value = v; }
        public StyleLength(float v) { value = new Length(v); }
        public static implicit operator StyleLength(float v) => new StyleLength(v);
        public static implicit operator StyleLength(Length v) => new StyleLength(v);
    }

    public struct StyleFloat
    {
        public float value { get; set; }
        public StyleFloat(float v) { value = v; }
        public static implicit operator StyleFloat(float v) => new StyleFloat(v);
    }

    public struct StyleColor
    {
        public UnityEngine.Color value { get; set; }
        public StyleColor(UnityEngine.Color c) { value = c; }
        public static implicit operator StyleColor(UnityEngine.Color c) => new StyleColor(c);
    }

    public struct StyleEnum<T> where T : struct, Enum
    {
        public T value { get; set; }
        public StyleEnum(T v) { value = v; }
        public static implicit operator StyleEnum<T>(T v) => new StyleEnum<T>(v);
    }

    public sealed class IStyle
    {
        public StyleEnum<FlexDirection> flexDirection { get; set; }
        public StyleEnum<Align> alignItems { get; set; }
        public StyleEnum<Justify> justifyContent { get; set; }
        public StyleEnum<Wrap> flexWrap { get; set; }
        public StyleFloat flexGrow { get; set; }
        public StyleFloat flexShrink { get; set; }
        public StyleLength width { get; set; }
        public StyleLength height { get; set; }
        public StyleLength minWidth { get; set; }
        public StyleLength minHeight { get; set; }
        public StyleLength maxHeight { get; set; }
        public StyleLength marginTop { get; set; }
        public StyleLength marginBottom { get; set; }
        public StyleLength marginLeft { get; set; }
        public StyleLength marginRight { get; set; }
        public StyleLength paddingTop { get; set; }
        public StyleLength paddingBottom { get; set; }
        public StyleLength paddingLeft { get; set; }
        public StyleLength paddingRight { get; set; }
        public StyleLength borderTopWidth { get; set; }
        public StyleLength borderBottomWidth { get; set; }
        public StyleLength borderLeftWidth { get; set; }
        public StyleLength borderRightWidth { get; set; }
        public StyleColor backgroundColor { get; set; }
        public StyleColor borderTopColor { get; set; }
        public StyleColor borderBottomColor { get; set; }
        public StyleColor borderLeftColor { get; set; }
        public StyleColor borderRightColor { get; set; }
        public StyleColor color { get; set; }
        public StyleLength fontSize { get; set; }
        public StyleEnum<WhiteSpace> whiteSpace { get; set; }
    }

    public class VisualElement
    {
        private readonly List<VisualElement> _children = new List<VisualElement>();
        public string name { get; set; } = string.Empty;
        public string tooltip { get; set; } = string.Empty;
        public IStyle style { get; } = new IStyle();
        public int childCount => _children.Count;
        public IReadOnlyList<VisualElement> Children => _children;
        public void Add(VisualElement child) { if (child != null) _children.Add(child); }
        public void Clear() => _children.Clear();
    }

    public class Label : VisualElement
    {
        public string text { get; set; }
        public Label() : this(string.Empty) { }
        public Label(string text) { this.text = text ?? string.Empty; }
    }

    public class Button : VisualElement
    {
        private readonly Action _clickEvent;
        public string text { get; set; }
        public Button() : this(null) { }
        public Button(Action clickEvent) { _clickEvent = clickEvent; }
        public void InvokeClick() => _clickEvent?.Invoke();
    }

    public sealed class ChangeEvent<T>
    {
        public T previousValue { get; set; }
        public T newValue { get; set; }
    }

    public delegate void EventCallback<in TEventType>(TEventType evt);

    public class TextField : VisualElement
    {
        private string _value = string.Empty;
        private EventCallback<ChangeEvent<string>> _callback;
        public string label { get; set; }
        public string value
        {
            get => _value;
            set
            {
                string prev = _value;
                _value = value ?? string.Empty;
                _callback?.Invoke(new ChangeEvent<string> { previousValue = prev, newValue = _value });
            }
        }
        public TextField() : this(string.Empty) { }
        public TextField(string label) { this.label = label; }
        public void SetValueWithoutNotify(string newValue) { _value = newValue ?? string.Empty; }
        public void RegisterValueChangedCallback(EventCallback<ChangeEvent<string>> callback) { _callback += callback; }
    }

    public class ScrollView : VisualElement { }

    public class ProgressBar : VisualElement
    {
        public string title { get; set; }
        public float lowValue { get; set; } = 0f;
        public float highValue { get; set; } = 100f;
        public float value { get; set; }
    }

    public class HelpBox : VisualElement
    {
        public string text { get; set; }
        public HelpBoxMessageType messageType { get; set; }
        public HelpBox() { }
        public HelpBox(string text, HelpBoxMessageType messageType)
        {
            this.text = text;
            this.messageType = messageType;
        }
    }
}
