using System.Collections.Generic;

namespace CS2WallText;

public class WallTextData
{
    /// <summary>Internal identifier used by add/edit/delete/list and as the menu/dictionary key. Not what's shown on the wall.</summary>
    public string Name { get; set; } = "";

    /// <summary>The actual displayed message - entered via chat so it can contain spaces.</summary>
    public string Text { get; set; } = "";

    public float[] Position { get; set; } = new float[3];

    /// <summary>Pitch, Yaw, Roll.</summary>
    public float[] Angles { get; set; } = new float[3];

    public bool Enabled { get; set; } = true;
    public string ColorHex { get; set; } = "#FFFFFF";
    public string FontName { get; set; } = "";
    public float FontSize { get; set; } = 24f;
    public float Scale { get; set; } = 0.4f;
    public bool Background { get; set; } = false;
    public bool Fullbright { get; set; } = true;
    public bool FacePlayer { get; set; } = false;

    /// <summary>0 = Left, 1 = Center, 2 = Right</summary>
    public int JustifyHorizontal { get; set; } = 1;

    /// <summary>0 = Bottom, 1 = Center, 2 = Top</summary>
    public int JustifyVertical { get; set; } = 1;

    public float DepthOffset { get; set; } = 0.5f;

    // --- RAINBOW / RGB EFFECT ---
    public bool Rainbow { get; set; } = false;
    public float RainbowSpeed { get; set; } = 1.0f;

    // --- ROTATING TEXT ANNOUNCER (DONEN DUYURU PANOSU) ---
    public bool IsRotating { get; set; } = false;
    public float RotationIntervalSeconds { get; set; } = 20.0f;
    public List<string> RotatingMessages { get; set; } = new();

    // --- INTERACTABLE BUTTON / TRIGGER (ETKILESIMLI BUTON) ---
    public bool IsInteractable { get; set; } = false;
    public string TriggerType { get; set; } = "both"; // "use", "shoot", "both"
    public string CommandToRun { get; set; } = "";
    public string MessageToPlayer { get; set; } = "";
    public float InteractionDistance { get; set; } = 300f;
    public float CooldownSeconds { get; set; } = 2.0f;
}
