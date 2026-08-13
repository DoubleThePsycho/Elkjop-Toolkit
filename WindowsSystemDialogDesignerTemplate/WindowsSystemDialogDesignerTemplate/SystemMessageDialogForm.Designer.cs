namespace WindowsSystemDialogDesignerTemplate;

partial class SystemMessageDialogForm
{
    private System.ComponentModel.IContainer? components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        contentPanel = new Panel();
        messageLabel = new Label();
        mainInstructionLabel = new Label();
        messageIcon = new PictureBox();
        footerPanel = new Panel();
        closeButton = new Button();
        separatorPanel = new Panel();
        contentPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)messageIcon).BeginInit();
        footerPanel.SuspendLayout();
        SuspendLayout();
        // 
        // contentPanel
        // 
        contentPanel.BackColor = Color.White;
        contentPanel.Controls.Add(messageLabel);
        contentPanel.Controls.Add(mainInstructionLabel);
        contentPanel.Controls.Add(messageIcon);
        contentPanel.Dock = DockStyle.Fill;
        contentPanel.Location = new Point(0, 0);
        contentPanel.Margin = new Padding(0);
        contentPanel.Name = "contentPanel";
        contentPanel.Size = new Size(500, 110);
        contentPanel.TabIndex = 0;
        // 
        // messageLabel
        // 
        messageLabel.BackColor = Color.Transparent;
        messageLabel.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        messageLabel.ForeColor = SystemColors.ControlText;
        messageLabel.Location = new Point(72, 53);
        messageLabel.Margin = new Padding(0);
        messageLabel.Name = "messageLabel";
        messageLabel.Size = new Size(404, 42);
        messageLabel.TabIndex = 2;
        messageLabel.Text = "Your computer has a low battery, so you should act immediately to keep from losing your work.";
        // 
        // mainInstructionLabel
        // 
        mainInstructionLabel.BackColor = Color.Transparent;
        mainInstructionLabel.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
        mainInstructionLabel.ForeColor = Color.FromArgb(0, 51, 153);
        mainInstructionLabel.Location = new Point(72, 18);
        mainInstructionLabel.Margin = new Padding(0);
        mainInstructionLabel.Name = "mainInstructionLabel";
        mainInstructionLabel.Size = new Size(404, 28);
        mainInstructionLabel.TabIndex = 1;
        mainInstructionLabel.Text = "Change your battery or switch to outlet power immediately";
        mainInstructionLabel.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // messageIcon
        // 
        messageIcon.BackColor = Color.Transparent;
        messageIcon.Image = SystemIcons.Warning.ToBitmap();
        messageIcon.Location = new Point(24, 21);
        messageIcon.Margin = new Padding(0);
        messageIcon.Name = "messageIcon";
        messageIcon.Size = new Size(32, 32);
        messageIcon.SizeMode = PictureBoxSizeMode.CenterImage;
        messageIcon.TabIndex = 0;
        messageIcon.TabStop = false;
        // 
        // footerPanel
        // 
        footerPanel.BackColor = Color.FromArgb(240, 240, 240);
        footerPanel.Controls.Add(closeButton);
        footerPanel.Controls.Add(separatorPanel);
        footerPanel.Dock = DockStyle.Bottom;
        footerPanel.Location = new Point(0, 110);
        footerPanel.Margin = new Padding(0);
        footerPanel.Name = "footerPanel";
        footerPanel.Size = new Size(500, 47);
        footerPanel.TabIndex = 1;
        // 
        // closeButton
        // 
        closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeButton.DialogResult = DialogResult.Cancel;
        closeButton.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        closeButton.Location = new Point(404, 12);
        closeButton.Margin = new Padding(0);
        closeButton.Name = "closeButton";
        closeButton.Size = new Size(75, 23);
        closeButton.TabIndex = 1;
        closeButton.Text = "Close";
        closeButton.UseVisualStyleBackColor = true;
        closeButton.Click += closeButton_Click;
        // 
        // separatorPanel
        // 
        separatorPanel.BackColor = Color.FromArgb(223, 223, 223);
        separatorPanel.Dock = DockStyle.Top;
        separatorPanel.Location = new Point(0, 0);
        separatorPanel.Margin = new Padding(0);
        separatorPanel.Name = "separatorPanel";
        separatorPanel.Size = new Size(500, 1);
        separatorPanel.TabIndex = 0;
        // 
        // SystemMessageDialogForm
        // 
        AcceptButton = closeButton;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;
        CancelButton = closeButton;
        ClientSize = new Size(500, 157);
        Controls.Add(contentPanel);
        Controls.Add(footerPanel);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "SystemMessageDialogForm";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Low Battery";
        contentPanel.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)messageIcon).EndInit();
        footerPanel.ResumeLayout(false);
        ResumeLayout(false);
    }

    #endregion

    private Panel contentPanel;
    private PictureBox messageIcon;
    private Label mainInstructionLabel;
    private Label messageLabel;
    private Panel footerPanel;
    private Panel separatorPanel;
    private Button closeButton;
}
