using SillyTavernTray.Ui.Controls;

namespace SillyTavernTray.Ui.Dialogs;

internal enum StopChoice { Wait, Force, Cancel }

internal sealed class ConfirmDialog : ThemedForm
{
    public enum Outcome { Confirm, Secondary, Cancel }

    private readonly Panel body;
    private readonly FlowLayoutPanel actions;

    public Outcome Result { get; private set; } = Outcome.Cancel;

    private ConfirmDialog(string title, string message, string primary, string? secondary, string cancel, bool primaryDanger = false)
        : base(resizable: false)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AccessibleName = title;

        var pad = Draw.Scale(this, 22);
        var width = Draw.Scale(this, 440);
        var bodyWidth = width - pad * 2;
        var titleHeight = TextRenderer.MeasureText(title, Typography.Title).Height;
        var bodyHeight = TextRenderer.MeasureText(message, Typography.Body,
            new Size(bodyWidth, 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        var buttonHeight = Draw.Scale(this, 34);
        var height = pad + titleHeight + Draw.Scale(this, 8) + bodyHeight + Draw.Scale(this, 24) + buttonHeight + pad;
        ClientSize = new Size(width, height);
        BackColor = Theme.Current.Background;

        body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Current.Background };
        Controls.Add(body);

        var titleLabel = new ThemedLabel
        {
            Text = title,
            Font = Typography.Title,
            Tone = LabelTone.Primary,
            AutoSize = false,
            Location = new Point(pad, pad),
            Size = new Size(bodyWidth, titleHeight + Draw.Scale(this, 4))
        };
        var messageLabel = new ThemedLabel
        {
            Text = message,
            Font = Typography.Body,
            Tone = LabelTone.Secondary,
            AutoSize = false,
            Location = new Point(pad, pad + titleHeight + Draw.Scale(this, 8)),
            Size = new Size(bodyWidth, bodyHeight + Draw.Scale(this, 2))
        };
        body.Controls.Add(titleLabel);
        body.Controls.Add(messageLabel);

        actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent
        };
        body.Controls.Add(actions);

        var force = secondary is null ? null : DialogButton(secondary, primaryDanger ? ButtonVariant.DangerAccent : ButtonVariant.DangerAccent, DialogResult.None);
        var primaryButton = DialogButton(primary, primaryDanger ? ButtonVariant.DangerAccent : ButtonVariant.Accent, DialogResult.OK);
        var cancelButton = DialogButton(cancel, ButtonVariant.Quiet, DialogResult.Cancel);
        cancelButton.DangerHover = false;

        if (force is not null)
        {
            force.Click += (_, _) => { Result = Outcome.Secondary; DialogResult = DialogResult.OK; Close(); };
            actions.Controls.Add(force);
        }
        primaryButton.Click += (_, _) => { Result = Outcome.Confirm; DialogResult = DialogResult.OK; Close(); };
        cancelButton.Click += (_, _) => { Result = Outcome.Cancel; DialogResult = DialogResult.Cancel; Close(); };
        actions.Controls.Add(primaryButton);
        actions.Controls.Add(cancelButton);

        AcceptButton = primaryButton;
        CancelButton = cancelButton;

        void PlaceActions()
        {
            actions.Location = new Point(
                Math.Max(pad, body.ClientSize.Width - actions.Width - pad),
                Math.Max(pad, body.ClientSize.Height - actions.Height - pad));
        }
        body.Resize += (_, _) => PlaceActions();
        PlaceActions();
    }

    private ThemedButton DialogButton(string text, ButtonVariant variant, DialogResult result)
    {
        var button = new ThemedButton
        {
            Text = text,
            Variant = variant,
            PreferredHeight = 34,
            Margin = new Padding(Draw.Scale(this, 8), 0, 0, 0),
            DialogResult = result
        };
        button.AutoSize = false;
        button.Size = button.GetPreferredSize(Size.Empty);
        button.AccessibleName = text;
        return button;
    }

    public static StopChoice AskStop(IWin32Window owner, string message)
    {
        using var dialog = new ConfirmDialog("服务尚未退出", message,
            primary: "继续等待", secondary: "强制结束", cancel: "取消操作");
        dialog.ShowDialog(owner);
        return dialog.Result switch
        {
            Outcome.Confirm => StopChoice.Wait,
            Outcome.Secondary => StopChoice.Force,
            _ => StopChoice.Cancel
        };
    }

    public static bool AskConfirm(IWin32Window owner, string title, string message, string primaryText, bool danger = false)
    {
        using var dialog = new ConfirmDialog(title, message, primaryText, secondary: null, cancel: "取消", primaryDanger: danger);
        dialog.ShowDialog(owner);
        return dialog.Result == Outcome.Confirm;
    }

    protected override void ApplyTheme()
    {
        if (body is not null) body.BackColor = Theme.Current.Background;
    }
}
