using System.Globalization;
using System.Text;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.Game;
using VRage.Input;
using VRage.Utils;
using VRageMath;
using Color = VRageMath.Color;

namespace BlueprintCollections;

/// <summary>
/// Single configuration screen: usage instructions at the top and the preview image cache
/// limit at the bottom.
/// </summary>
internal sealed class ConfigScreen : MyGuiScreenBase
{
    private readonly MyGuiControlTextbox m_limitTextbox;
    private readonly MyGuiControlLabel m_errorLabel;
    private readonly int m_maxLimit;

    public ConfigScreen()
        : base(
            new Vector2(0.5f, 0.5f),
            MyGuiConstants.SCREEN_BACKGROUND_COLOR,
            new Vector2(0.62f, 0.56f),
            isTopMostScreen: false)
    {
        CanHideOthers = false;
        EnabledBackgroundFade = true;
        CloseButtonEnabled = true;
        m_maxLimit = Config.MaxPreviewCacheLimitMb;

        AddCaption("Workshop Collection Filter");

        Controls.Add(new MyGuiControlMultilineText(
            position: new Vector2(0f, -0.1f),
            size: new Vector2(0.54f, 0.2f),
            font: "Blue",
            textScale: 0.7f,
            textAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            contents: new StringBuilder(
                "The filter is part of the Blueprints screen.\n\n" +
                "Open the Blueprints screen (F10), switch the group filter to the Steam " +
                "Workshop or All view, then pick a collection in the \"Workshop collection\" " +
                "picker above the search box.\n\n" +
                "Owned, subscribed and favorited collections are listed. Selecting " +
                "\"All Subscribed Items\" turns the filter off."),
            drawScrollbarV: false,
            drawScrollbarH: false,
            textBoxAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));

        var separator = new MyGuiControlSeparatorList();
        separator.AddHorizontal(new Vector2(-0.27f, 0.03f), 0.54f);
        Controls.Add(separator);

        Controls.Add(new MyGuiControlLabel(
            position: new Vector2(0f, 0.08f),
            text: "Preview cache limit (MB, 0 = unlimited)",
            textScale: 0.8f,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER));

        m_errorLabel = new MyGuiControlLabel(
            position: new Vector2(0f, 0.17f),
            text: "Enter a whole number between 0 and " + m_maxLimit + ".",
            colorMask: Color.Red.ToVector4(),
            textScale: 0.7f,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER)
        {
            Visible = false
        };
        Controls.Add(m_errorLabel);

        m_limitTextbox = new MyGuiControlTextbox(
            position: new Vector2(0f, 0.125f),
            defaultText: Config.Current.PreviewCacheLimitMb.ToString(CultureInfo.InvariantCulture),
            maxLength: 4,
            textScale: 0.8f,
            type: MyGuiControlTextboxType.DigitsOnly)
        {
            Size = new Vector2(0.12f, 0.04f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER
        };
        m_limitTextbox.TextChanged += delegate(MyGuiControlTextbox textbox)
        {
            textbox.ColorMask = Vector4.One;
            m_errorLabel.Visible = false;
        };
        Controls.Add(m_limitTextbox);

        Controls.Add(new MyGuiControlButton(
            position: new Vector2(-0.09f, 0.225f),
            text: MyTexts.Get(MyCommonTexts.Ok),
            onButtonClick: OnOkClicked));

        Controls.Add(new MyGuiControlButton(
            position: new Vector2(0.09f, 0.225f),
            text: MyTexts.Get(MyCommonTexts.Cancel),
            onButtonClick: OnCancelClicked));

        FocusedControl = m_limitTextbox;
        m_limitTextbox.SelectAll();
    }

    public override string GetFriendlyName()
    {
        return "BlueprintCollectionsConfigScreen";
    }

    public override void HandleUnhandledInput(bool receivedFocusInThisUpdate)
    {
        base.HandleUnhandledInput(receivedFocusInThisUpdate);

        if (MyInput.Static.IsNewKeyPressed(MyKeys.Enter))
        {
            Confirm();
        }
    }

    private void OnOkClicked(MyGuiControlButton sender)
    {
        Confirm();
    }

    private void OnCancelClicked(MyGuiControlButton sender)
    {
        CloseScreen();
    }

    private void Confirm()
    {
        if (!int.TryParse(
                m_limitTextbox.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value) ||
            value < 0 || value > m_maxLimit)
        {
            m_limitTextbox.ColorMask = Color.Red.ToVector4();
            m_errorLabel.Visible = true;
            FocusedControl = m_limitTextbox;
            m_limitTextbox.SelectAll();
            return;
        }

        Config.Current.PreviewCacheLimitMb = value;
        Config.Save();
        WorkshopCollectionService.EnforcePreviewCacheLimit();
        CloseScreen();
    }
}
