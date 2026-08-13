namespace Bypass_OOBE.Forms;

partial class SetupForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components is not null)
        {
            components.Dispose();
        }

        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(SetupForm));
        contentPanel = new Panel();
        titleLabel = new Label();
        descriptionLabel = new Label();
        groupBox1 = new GroupBox();
        usernameBox = new TextBox();
        groupBox2 = new GroupBox();
        passwordBox = new TextBox();
        revealIcon = new PictureBox();
        passwordErrorBorder = new Panel();
        groupBox3 = new GroupBox();
        confirmPasswordBox = new TextBox();
        confirmPasswordErrorBorder = new Panel();
        groupBox4 = new GroupBox();
        languageCombo = new ComboBox();
        keyboardLabel = new Label();
        detailsLabel = new Label();
        cancelButton = new Button();
        startButton = new Button();
        eyeToolTip = new ToolTip(components);
        errorTimer = new System.Windows.Forms.Timer(components);
        contentPanel.SuspendLayout();
        groupBox1.SuspendLayout();
        groupBox2.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)revealIcon).BeginInit();
        groupBox3.SuspendLayout();
        groupBox4.SuspendLayout();
        SuspendLayout();
        // 
        // contentPanel
        // 
        contentPanel.Controls.Add(titleLabel);
        contentPanel.Controls.Add(descriptionLabel);
        contentPanel.Controls.Add(groupBox1);
        contentPanel.Controls.Add(groupBox2);
        contentPanel.Controls.Add(groupBox3);
        contentPanel.Controls.Add(groupBox4);
        contentPanel.Controls.Add(keyboardLabel);
        contentPanel.Controls.Add(detailsLabel);
        contentPanel.Controls.Add(cancelButton);
        contentPanel.Controls.Add(startButton);
        contentPanel.Dock = DockStyle.Fill;
        contentPanel.Location = new Point(0, 0);
        contentPanel.Margin = new Padding(4, 4, 4, 4);
        contentPanel.Name = "contentPanel";
        contentPanel.Padding = new Padding(38, 25, 38, 12);
        contentPanel.Size = new Size(482, 591);
        contentPanel.TabIndex = 0;
        // 
        // titleLabel
        // 
        titleLabel.AutoSize = true;
        titleLabel.Font = new Font("Segoe UI Semibold", 16F);
        titleLabel.Location = new Point(41, 25);
        titleLabel.Margin = new Padding(4, 0, 4, 0);
        titleLabel.Name = "titleLabel";
        titleLabel.Size = new Size(282, 37);
        titleLabel.TabIndex = 0;
        titleLabel.Text = "Windows OOBE setup";
        // 
        // descriptionLabel
        // 
        descriptionLabel.AutoSize = true;
        descriptionLabel.Location = new Point(41, 62);
        descriptionLabel.Margin = new Padding(4, 0, 4, 0);
        descriptionLabel.Name = "descriptionLabel";
        descriptionLabel.Size = new Size(401, 20);
        descriptionLabel.TabIndex = 1;
        descriptionLabel.Text = "Create administrator account and choose display language.";
        // 
        // groupBox1
        // 
        groupBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        groupBox1.AutoSize = true;
        groupBox1.Controls.Add(usernameBox);
        groupBox1.Location = new Point(41, 106);
        groupBox1.Margin = new Padding(4, 25, 4, 25);
        groupBox1.Name = "groupBox1";
        groupBox1.Padding = new Padding(4, 4, 4, 4);
        groupBox1.Size = new Size(400, 65);
        groupBox1.TabIndex = 2;
        groupBox1.TabStop = false;
        groupBox1.Text = "Username";
        // 
        // usernameBox
        // 
        usernameBox.Dock = DockStyle.Fill;
        usernameBox.Location = new Point(4, 24);
        usernameBox.Margin = new Padding(4, 4, 4, 4);
        usernameBox.Name = "usernameBox";
        usernameBox.PlaceholderText = "Username";
        usernameBox.Size = new Size(392, 27);
        usernameBox.TabIndex = 0;
        usernameBox.Text = "Bruker";
        // 
        // groupBox2
        // 
        groupBox2.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        groupBox2.Controls.Add(passwordBox);
        groupBox2.Controls.Add(revealIcon);
        groupBox2.Controls.Add(passwordErrorBorder);
        groupBox2.Location = new Point(41, 200);
        groupBox2.Margin = new Padding(4, 4, 4, 4);
        groupBox2.Name = "groupBox2";
        groupBox2.Padding = new Padding(4, 4, 4, 4);
        groupBox2.Size = new Size(400, 65);
        groupBox2.TabIndex = 3;
        groupBox2.TabStop = false;
        groupBox2.Text = "Password (optional)";
        // 
        // passwordBox
        // 
        passwordBox.Dock = DockStyle.Fill;
        passwordBox.Location = new Point(4, 24);
        passwordBox.Margin = new Padding(4, 4, 4, 4);
        passwordBox.MaxLength = 127;
        passwordBox.Name = "passwordBox";
        passwordBox.PasswordChar = '●';
        passwordBox.PlaceholderText = "Password";
        passwordBox.Size = new Size(392, 27);
        passwordBox.TabIndex = 0;
        passwordBox.TextChanged += PasswordBox_TextChanged;
        // 
        // revealIcon
        // 
        revealIcon.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        revealIcon.BackColor = SystemColors.Window;
        revealIcon.Cursor = Cursors.Hand;
        revealIcon.Location = new Point(370, 28);
        revealIcon.Margin = new Padding(4, 4, 4, 4);
        revealIcon.Name = "revealIcon";
        revealIcon.Size = new Size(22, 22);
        revealIcon.SizeMode = PictureBoxSizeMode.CenterImage;
        revealIcon.TabIndex = 1;
        revealIcon.TabStop = false;
        eyeToolTip.SetToolTip(revealIcon, "Show password");
        revealIcon.Click += RevealIcon_Click;
        // 
        // passwordErrorBorder
        // 
        passwordErrorBorder.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        passwordErrorBorder.BackColor = Color.FromArgb(196, 43, 28);
        passwordErrorBorder.Location = new Point(4, 52);
        passwordErrorBorder.Margin = new Padding(4, 4, 4, 4);
        passwordErrorBorder.Name = "passwordErrorBorder";
        passwordErrorBorder.Size = new Size(392, 2);
        passwordErrorBorder.TabIndex = 2;
        passwordErrorBorder.Visible = false;
        // 
        // groupBox3
        // 
        groupBox3.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        groupBox3.Controls.Add(confirmPasswordBox);
        groupBox3.Controls.Add(confirmPasswordErrorBorder);
        groupBox3.Location = new Point(41, 272);
        groupBox3.Margin = new Padding(4, 4, 4, 4);
        groupBox3.Name = "groupBox3";
        groupBox3.Padding = new Padding(4, 4, 4, 4);
        groupBox3.Size = new Size(400, 65);
        groupBox3.TabIndex = 4;
        groupBox3.TabStop = false;
        groupBox3.Text = "Confirm password";
        // 
        // confirmPasswordBox
        // 
        confirmPasswordBox.Dock = DockStyle.Fill;
        confirmPasswordBox.Location = new Point(4, 24);
        confirmPasswordBox.Margin = new Padding(4, 4, 4, 4);
        confirmPasswordBox.MaxLength = 127;
        confirmPasswordBox.Name = "confirmPasswordBox";
        confirmPasswordBox.PasswordChar = '●';
        confirmPasswordBox.PlaceholderText = "Password";
        confirmPasswordBox.Size = new Size(392, 27);
        confirmPasswordBox.TabIndex = 0;
        confirmPasswordBox.TextChanged += ConfirmPasswordBox_TextChanged;
        // 
        // confirmPasswordErrorBorder
        // 
        confirmPasswordErrorBorder.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        confirmPasswordErrorBorder.BackColor = Color.FromArgb(196, 43, 28);
        confirmPasswordErrorBorder.Location = new Point(4, 52);
        confirmPasswordErrorBorder.Margin = new Padding(4, 4, 4, 4);
        confirmPasswordErrorBorder.Name = "confirmPasswordErrorBorder";
        confirmPasswordErrorBorder.Size = new Size(392, 2);
        confirmPasswordErrorBorder.TabIndex = 1;
        confirmPasswordErrorBorder.Visible = false;
        // 
        // groupBox4
        // 
        groupBox4.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        groupBox4.Controls.Add(languageCombo);
        groupBox4.Location = new Point(41, 366);
        groupBox4.Margin = new Padding(4, 25, 4, 4);
        groupBox4.Name = "groupBox4";
        groupBox4.Padding = new Padding(4, 4, 4, 4);
        groupBox4.Size = new Size(400, 65);
        groupBox4.TabIndex = 7;
        groupBox4.TabStop = false;
        groupBox4.Text = "Windows display language";
        // 
        // languageCombo
        // 
        languageCombo.DisplayMember = "DisplayName";
        languageCombo.Dock = DockStyle.Fill;
        languageCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        languageCombo.Location = new Point(4, 24);
        languageCombo.Margin = new Padding(4, 4, 4, 4);
        languageCombo.Name = "languageCombo";
        languageCombo.Size = new Size(392, 28);
        languageCombo.TabIndex = 0;
        // 
        // keyboardLabel
        // 
        keyboardLabel.AutoSize = true;
        keyboardLabel.ForeColor = SystemColors.GrayText;
        keyboardLabel.Location = new Point(41, 435);
        keyboardLabel.Margin = new Padding(4, 0, 4, 0);
        keyboardLabel.Name = "keyboardLabel";
        keyboardLabel.Size = new Size(401, 20);
        keyboardLabel.TabIndex = 8;
        keyboardLabel.Text = "Keyboard and regional format: Norwegian (Bokmål) - fixed";
        // 
        // detailsLabel
        // 
        detailsLabel.AutoEllipsis = true;
        detailsLabel.AutoSize = true;
        detailsLabel.ForeColor = SystemColors.GrayText;
        detailsLabel.Location = new Point(41, 454);
        detailsLabel.Margin = new Padding(4, 0, 4, 0);
        detailsLabel.Name = "detailsLabel";
        detailsLabel.Size = new Size(266, 20);
        detailsLabel.TabIndex = 9;
        detailsLabel.Text = "Windows information will appear here.";
        // 
        // cancelButton
        // 
        cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Location = new Point(159, 536);
        cancelButton.Margin = new Padding(4, 4, 4, 4);
        cancelButton.Name = "cancelButton";
        cancelButton.Size = new Size(125, 40);
        cancelButton.TabIndex = 10;
        cancelButton.Text = "Cancel";
        // 
        // startButton
        // 
        startButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        startButton.Location = new Point(291, 535);
        startButton.Margin = new Padding(4, 4, 4, 4);
        startButton.Name = "startButton";
        startButton.Size = new Size(150, 40);
        startButton.TabIndex = 11;
        startButton.Text = "Start setup";
        startButton.Click += StartButton_Click;
        // 
        // errorTimer
        // 
        errorTimer.Interval = 3000;
        errorTimer.Tick += ErrorTimer_Tick;
        // 
        // SetupForm
        // 
        AcceptButton = startButton;
        AutoScaleDimensions = new SizeF(120F, 120F);
        AutoScaleMode = AutoScaleMode.Dpi;
        CancelButton = cancelButton;
        ClientSize = new Size(482, 591);
        Controls.Add(contentPanel);
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = (Icon)resources.GetObject("$this.Icon");
        Margin = new Padding(4, 4, 4, 4);
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "SetupForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Elkjop OOBE Setup";
        FormClosed += SetupForm_FormClosed;
        Shown += SetupForm_Shown;
        contentPanel.ResumeLayout(false);
        contentPanel.PerformLayout();
        groupBox1.ResumeLayout(false);
        groupBox1.PerformLayout();
        groupBox2.ResumeLayout(false);
        groupBox2.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)revealIcon).EndInit();
        groupBox3.ResumeLayout(false);
        groupBox3.PerformLayout();
        groupBox4.ResumeLayout(false);
        ResumeLayout(false);
    }

    #endregion

    private System.Windows.Forms.Panel contentPanel = null!;
    private System.Windows.Forms.Label titleLabel = null!;
    private System.Windows.Forms.ComboBox languageCombo = null!;
    private System.Windows.Forms.Label keyboardLabel = null!;
    private System.Windows.Forms.Label detailsLabel = null!;
    private System.Windows.Forms.Button startButton = null!;
    private System.Windows.Forms.Button cancelButton = null!;
    private System.Windows.Forms.ToolTip eyeToolTip = null!;
    private System.Windows.Forms.Timer errorTimer = null!;
    private GroupBox groupBox4 = null!;
    private Label descriptionLabel = null!;
    private GroupBox groupBox1 = null!;
    private TextBox usernameBox = null!;
    private GroupBox groupBox2 = null!;
    private TextBox passwordBox = null!;
    private Panel passwordErrorBorder = null!;
    private PictureBox revealIcon = null!;
    private GroupBox groupBox3 = null!;
    private TextBox confirmPasswordBox = null!;
    private Panel confirmPasswordErrorBorder = null!;
}
