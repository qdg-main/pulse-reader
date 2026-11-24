namespace PulseReader.Settings;

internal sealed class DistributionTermsForm : Form
{
    public DistributionTermsForm(string termsText, bool isUpdate = false)
    {
        Text = "Pulse Reader distribution terms";
        AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(740, 600);
        MinimumSize = new Size(640, 500);
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PulseReaderTheme.Window;
        ForeColor = PulseReaderTheme.Text;
        Font = new Font("Segoe UI", 10f);

        var description = new Label
        {
            Text = isUpdate
                ? "Review the distribution terms for this update. Cancelling leaves your current version running."
                : "Read the distribution terms below. Select the agreement checkbox to continue using this packaged copy of Pulse Reader.",
            Location = new Point(20, 18), Size = new Size(700, 48),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        var terms = new RichTextBox
        {
            Name = "DistributionTermsText", AccessibleName = "Complete distribution terms",
            Text = termsText, ReadOnly = true, DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = true,
            BackColor = PulseReaderTheme.Surface, ForeColor = PulseReaderTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(20, 76), Size = new Size(700, 390),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            TabIndex = 0
        };
        var agreement = new CheckBox
        {
            Name = "DistributionTermsAgreement", AccessibleName = "I agree to the distribution terms",
            Text = "I have read and agree to these distribution terms.",
            Checked = false, AutoSize = false, UseVisualStyleBackColor = false,
            BackColor = PulseReaderTheme.Window, ForeColor = PulseReaderTheme.Text,
            Location = new Point(20, 478), Size = new Size(700, 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            TabIndex = 1
        };
        var declineLabel = isUpdate ? "Cancel update" : "Decline and exit";
        var decline = new GlowButton
        {
            Name = "DeclineDistributionTerms", AccessibleName = declineLabel,
            Text = declineLabel, DialogResult = DialogResult.Cancel,
            Location = new Point(420, 540), Size = new Size(150, 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right, TabIndex = 2
        };
        var proceed = new GlowButton
        {
            Name = "AcceptDistributionTerms", AccessibleName = "Accept and continue",
            Text = "Continue", Enabled = false,
            Location = new Point(580, 540), Size = new Size(140, 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right, TabIndex = 3
        };
        agreement.CheckedChanged += (_, _) => proceed.Enabled = agreement.Checked;
        proceed.Click += (_, _) =>
        {
            if (!agreement.Checked) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        AcceptButton = proceed;
        CancelButton = decline;
        Controls.AddRange([description, terms, agreement, decline, proceed]);
    }
}
