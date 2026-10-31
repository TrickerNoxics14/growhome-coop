using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CoopMod
{
    // Text entry the game's way: a uGUI InputField (Unity 4.6 has one) with the game's font, sitting inside a cloned menu line.
    // The menu line stays the selected object (so the game's own Animator draws the pink highlight and ">>>"); the line's CoopTextRow hands the
    // keyboard events of each frame to this field while it is typing. The field itself is never selected and never hit by the mouse
    // (its CanvasGroup blocks no raycasts, its navigation is None).
    //
    // Unity 4.6's onEndEdit fires on Enter, Esc and on losing the selection alike, so it is never used as "send": Feed() classifies every key with
    // the same CoopUI.ChatKeyAction the chat box always used, and reports Enter / Esc / leaving the line to the owner.
    public class CoopField : InputField
    {
        public const int None = 0, Submit = 1, Cancel = 2, Up = 3, Down = 4;

        public static CoopField Focused;                       // the field typing right now (one at a time)
        public static bool AnyFocused { get { return Focused != null && Focused.isFocused; } }

        public string Allowed;                                 // null: any character the font has; else only these (after upper-casing)
        public bool UpperCase = true;
        public int OpenedFrame = -10;                          // keys of the frame the field was opened in are never typed (the key that opened it)
        int activateFrame = -1;
        readonly Event popped = new Event();

        protected override void Awake()
        {
            base.Awake();
            onValidateInput = ValidateCode;
        }

        char ValidateCode(string text, int pos, char c)
        {
            if (UpperCase) c = char.ToUpperInvariant(c);
            if (c < ' ') return '\0';
            if (Allowed != null && Allowed.IndexOf(c) < 0) return '\0';
            var t = textComponent;
            if (t != null && t.font != null && !t.font.HasCharacter(c)) return '\0';     // the font would drop it silently
            return c;
        }

        // Typing starts on the NEXT frame, so the key that opened the field is not typed into it.
        public void Open() { OpenedFrame = Time.frameCount; activateFrame = Time.frameCount + 1; }

        public void Close()
        {
            activateFrame = -1;
            if (isFocused) DeactivateInputField();
            if (Focused == this) Focused = null;
        }

        public bool Opening { get { return activateFrame >= 0; } }

        // every frame from the owner: starts typing when it is time
        public void Tick()
        {
            if (activateFrame >= 0 && Time.frameCount >= activateFrame)
            {
                activateFrame = -1;
                ActivateInputField();
                if (isFocused) { Focused = this; MoveTextEnd(false); }
            }
            if (Focused == this && !isFocused) Focused = null;
        }

        // Drains this frame's keyboard events into the field. Returns what the owner must do (None = keep typing).
        public int Pump()
        {
            if (!isFocused) return None;
            int result = None; bool any = false;
            while (Event.PopEvent(popped))
            {
                if (popped.rawType != EventType.KeyDown) continue;
                any = true;
                result = Feed(popped);
                if (result != None) break;
            }
            if (any) UpdateLabel();
            return result;
        }

        // One key event, exactly as Pump handles it (the audit types through this).
        public int Feed(Event e)
        {
            if (e == null || e.type != EventType.KeyDown) return None;
            if (Time.frameCount <= OpenedFrame) return None;
            int act = CoopUI.ChatKeyAction(e);
            if (act == 1) return Submit;
            if (act == 2) return Cancel;
            if (e.keyCode == KeyCode.UpArrow) return Up;
            if (e.keyCode == KeyCode.DownArrow || e.keyCode == KeyCode.Tab) return Down;
            if (UpperCase && e.character != '\0') e.character = char.ToUpperInvariant(e.character);   // the game font may lack lower case: upper-case BEFORE the field checks the glyph
            ProcessEvent(e);
            return None;
        }

        public int FeedForTest(Event e)
        {
            if (!isFocused) { activateFrame = -1; ActivateInputField(); if (isFocused) Focused = this; }
            OpenedFrame = -10;
            int r = Feed(e);
            UpdateLabel();
            return r;
        }

        public int TypeForTest(string s)
        {
            int r = None;
            foreach (char c in s) { var e = new Event { type = EventType.KeyDown, character = c, keyCode = KeyCode.None }; r = FeedForTest(e); if (r != None) break; }
            return r;
        }

        protected override void OnDisable()
        {
            activateFrame = -1;
            if (Focused == this) Focused = null;
            base.OnDisable();
        }
    }

    // On the menu line that holds a CoopField: the input module sends the selected object its per-frame update; while the field types, the line
    // takes the keys (and Uses the event, so the module does not also move the selection or press the line).
    public class CoopTextRow : MonoBehaviour, IUpdateSelectedHandler, ISelectHandler, IDeselectHandler
    {
        public CoopMenuHandler Handler;

        public void OnUpdateSelected(BaseEventData eventData) { if (Handler != null && Handler.PumpTyping()) eventData.Use(); }
        public void OnSelect(BaseEventData eventData) { if (Handler != null) Handler.TextRowSelected(); }
        public void OnDeselect(BaseEventData eventData) { if (Handler != null) Handler.TextRowDeselected(); }
    }
}
