using System.Reflection;
using System.Text;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Audio;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace BlueprintCollections;

/// <summary>
/// Combobox that jumps to the next entry starting with the pressed letter or digit, in addition
/// to the game's normal arrow/Page/Home/End navigation.
/// </summary>
internal sealed class CollectionFilterCombobox : MyGuiControlCombobox
{
    private static readonly FieldInfo KeyboardIndexField =
        typeof(MyGuiControlCombobox).GetField(
            "m_preselectedKeyboardIndex",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo ScrollToIndexMethod =
        typeof(MyGuiControlCombobox).GetMethod(
            "SetScrollBarPositionByIndex",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(long) },
            null);

    public CollectionFilterCombobox(
        Vector2? position = null,
        Vector2? size = null,
        int openAreaItemsCount = 10,
        string toolTip = null,
        MyGuiDrawAlignEnum originAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER)
        : base(
            position,
            size,
            openAreaItemsCount: openAreaItemsCount,
            toolTip: toolTip,
            originAlign: originAlign)
    {
    }

    public override MyGuiControlBase HandleInput()
    {
        MyGuiControlBase handled = base.HandleInput();

        if (!Enabled || !HasFocus) return handled;

        char typed = GetTypedCharacter();
        if (typed == '\0') return handled;

        if (!IsOpen) IsOpen = true;

        int next = FindNextIndex(typed, GetCurrentIndex());
        if (next < 0) return handled;

        KeyboardIndexField?.SetValue(this, (int?)next);
        ScrollToIndexMethod?.Invoke(this, new object[] { (long)next });
        MyGuiSoundManager.PlaySound(GuiSounds.MouseOver);
        return this;
    }

    private int GetCurrentIndex()
    {
        if (IsOpen && KeyboardIndexField != null)
        {
            object value = KeyboardIndexField.GetValue(this);
            if (value is int index) return index;
        }

        return GetSelectedIndex();
    }

    private int FindNextIndex(char typed, int current)
    {
        int count = GetItemsCount();
        if (count == 0) return -1;

        int start = current >= 0 ? (current + 1) % count : 0;
        for (int offset = 0; offset < count; offset++)
        {
            int index = (start + offset) % count;
            MyGuiControlCombobox.Item item = GetItemByIndex(index);
            if (item?.Value == null || item.Value.Length == 0) continue;

            if (StartsWithTypedCharacter(item.Value, typed))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Matches the first letter of a leading faction tag ("[SDX] Cool ships" for S) as well as
    /// the first letter after the tag ("... Cool ships" for C). The brackets themselves never
    /// need to be typed.
    /// </summary>
    private static bool StartsWithTypedCharacter(StringBuilder value, char typed)
    {
        if (value == null || value.Length == 0) return false;

        char target = char.ToUpperInvariant(typed);

        int index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index])) index++;

        // First alphanumeric character, including one inside a leading [tag].
        int first = index;
        while (first < value.Length && !char.IsLetterOrDigit(value[first])) first++;
        if (first < value.Length && char.ToUpperInvariant(value[first]) == target) return true;

        // First alphanumeric character after all leading [tag] groups and punctuation.
        while (true)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
            if (index >= value.Length || value[index] != '[') break;

            int close = -1;
            for (int i = index + 1; i < value.Length; i++)
            {
                if (value[i] == ']')
                {
                    close = i;
                    break;
                }
            }

            if (close < 0) break;
            index = close + 1;
        }

        while (index < value.Length && !char.IsLetterOrDigit(value[index])) index++;
        return index < value.Length && char.ToUpperInvariant(value[index]) == target;
    }

    private static char GetTypedCharacter()
    {
        if (MyInput.Static == null) return '\0';
        if (MyInput.Static.IsAnyCtrlKeyPressed() || MyInput.Static.IsAnyAltKeyPressed()) return '\0';

        for (int key = (int)MyKeys.D0; key <= (int)MyKeys.D9; key++)
        {
            if (MyInput.Static.IsNewKeyPressed((MyKeys)key)) return (char)key;
        }

        for (int key = (int)MyKeys.NumPad0; key <= (int)MyKeys.NumPad9; key++)
        {
            if (MyInput.Static.IsNewKeyPressed((MyKeys)key))
            {
                return (char)('0' + (key - (int)MyKeys.NumPad0));
            }
        }

        for (int key = (int)MyKeys.A; key <= (int)MyKeys.Z; key++)
        {
            if (MyInput.Static.IsNewKeyPressed((MyKeys)key)) return (char)key;
        }

        return '\0';
    }
}
