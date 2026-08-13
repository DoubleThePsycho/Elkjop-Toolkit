namespace Setup_Agent.Forms
{
    partial class InternetAccessForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            waitingPanel = new Panel();
            label1 = new Label();
            panel1 = new Panel();
            panel2 = new Panel();
            waitingBodyLabel = new Label();
            waitingIcon = new PictureBox();
            connectivityPollTimer = new System.Windows.Forms.Timer(components);
            countdownTimer = new System.Windows.Forms.Timer(components);
            establishedIcon = new PictureBox();
            establishedBodyLabel = new Label();
            mainInstructionLabel = new Label();
            establishedFooterPanel = new Panel();
            establishedSeparator = new Panel();
            okButton = new Button();
            cancelButton = new Button();
            establishedContentPanel = new Panel();
            waitingPanel.SuspendLayout();
            panel1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)waitingIcon).BeginInit();
            ((System.ComponentModel.ISupportInitialize)establishedIcon).BeginInit();
            establishedFooterPanel.SuspendLayout();
            establishedContentPanel.SuspendLayout();
            SuspendLayout();
            // 
            // waitingPanel
            // 
            waitingPanel.BackColor = Color.White;
            waitingPanel.Controls.Add(label1);
            waitingPanel.Controls.Add(panel1);
            waitingPanel.Controls.Add(waitingBodyLabel);
            waitingPanel.Controls.Add(waitingIcon);
            waitingPanel.Dock = DockStyle.Fill;
            waitingPanel.Location = new Point(0, 0);
            waitingPanel.Name = "waitingPanel";
            waitingPanel.Size = new Size(384, 136);
            waitingPanel.TabIndex = 0;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.BackColor = Color.Transparent;
            label1.Font = new Font("Segoe UI", 12F);
            label1.ForeColor = Color.FromArgb(0, 51, 153);
            label1.ImageAlign = ContentAlignment.TopLeft;
            label1.Location = new Point(80, 20);
            label1.Margin = new Padding(0);
            label1.Name = "label1";
            label1.Size = new Size(232, 21);
            label1.TabIndex = 4;
            label1.Text = "No internet connection detected";
            // 
            // panel1
            // 
            panel1.BackColor = Color.FromArgb(240, 240, 240);
            panel1.Controls.Add(panel2);
            panel1.Dock = DockStyle.Bottom;
            panel1.Location = new Point(0, 89);
            panel1.Margin = new Padding(0);
            panel1.Name = "panel1";
            panel1.Size = new Size(384, 47);
            panel1.TabIndex = 3;
            // 
            // panel2
            // 
            panel2.BackColor = Color.FromArgb(223, 223, 223);
            panel2.Dock = DockStyle.Top;
            panel2.Location = new Point(0, 0);
            panel2.Margin = new Padding(0);
            panel2.Name = "panel2";
            panel2.Size = new Size(384, 1);
            panel2.TabIndex = 0;
            // 
            // waitingBodyLabel
            // 
            waitingBodyLabel.AutoSize = true;
            waitingBodyLabel.Location = new Point(80, 50);
            waitingBodyLabel.Name = "waitingBodyLabel";
            waitingBodyLabel.Size = new Size(279, 15);
            waitingBodyLabel.TabIndex = 1;
            waitingBodyLabel.Text = "Please connect an Ethernet cable to continue setup.";
            // 
            // waitingIcon
            // 
            waitingIcon.Image = SystemIcons.Warning.ToBitmap();
            waitingIcon.Location = new Point(20, 20);
            waitingIcon.Name = "waitingIcon";
            waitingIcon.Size = new Size(40, 40);
            waitingIcon.SizeMode = PictureBoxSizeMode.StretchImage;
            waitingIcon.TabIndex = 2;
            waitingIcon.TabStop = false;
            // 
            // connectivityPollTimer
            // 
            connectivityPollTimer.Interval = 3000;
            connectivityPollTimer.Tick += ConnectivityPollTimer_Tick;
            // 
            // countdownTimer
            // 
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += CountdownTimer_Tick;
            // 
            // establishedIcon
            // 
            establishedIcon.Image = SystemIcons.Information.ToBitmap();
            establishedIcon.Location = new Point(20, 20);
            establishedIcon.Name = "establishedIcon";
            establishedIcon.Size = new Size(40, 40);
            establishedIcon.SizeMode = PictureBoxSizeMode.StretchImage;
            establishedIcon.TabIndex = 2;
            establishedIcon.TabStop = false;
            // 
            // establishedBodyLabel
            // 
            establishedBodyLabel.AutoSize = true;
            establishedBodyLabel.Location = new Point(80, 50);
            establishedBodyLabel.Name = "establishedBodyLabel";
            establishedBodyLabel.Size = new Size(166, 15);
            establishedBodyLabel.TabIndex = 1;
            establishedBodyLabel.Text = "Press Cancel within 5 to abort.";
            // 
            // mainInstructionLabel
            // 
            mainInstructionLabel.AutoSize = true;
            mainInstructionLabel.BackColor = Color.Transparent;
            mainInstructionLabel.Font = new Font("Segoe UI", 12F);
            mainInstructionLabel.ForeColor = Color.FromArgb(0, 51, 153);
            mainInstructionLabel.ImageAlign = ContentAlignment.TopLeft;
            mainInstructionLabel.Location = new Point(80, 20);
            mainInstructionLabel.Margin = new Padding(0);
            mainInstructionLabel.Name = "mainInstructionLabel";
            mainInstructionLabel.Size = new Size(227, 21);
            mainInstructionLabel.TabIndex = 3;
            mainInstructionLabel.Text = "Proceeding to update Windows";
            // 
            // establishedFooterPanel
            // 
            establishedFooterPanel.BackColor = Color.FromArgb(240, 240, 240);
            establishedFooterPanel.Controls.Add(cancelButton);
            establishedFooterPanel.Controls.Add(okButton);
            establishedFooterPanel.Controls.Add(establishedSeparator);
            establishedFooterPanel.Dock = DockStyle.Bottom;
            establishedFooterPanel.Location = new Point(0, 89);
            establishedFooterPanel.Margin = new Padding(0);
            establishedFooterPanel.Name = "establishedFooterPanel";
            establishedFooterPanel.Size = new Size(384, 47);
            establishedFooterPanel.TabIndex = 1;
            // 
            // establishedSeparator
            // 
            establishedSeparator.BackColor = Color.FromArgb(223, 223, 223);
            establishedSeparator.Dock = DockStyle.Top;
            establishedSeparator.Location = new Point(0, 0);
            establishedSeparator.Margin = new Padding(0);
            establishedSeparator.Name = "establishedSeparator";
            establishedSeparator.Size = new Size(384, 1);
            establishedSeparator.TabIndex = 0;
            // 
            // okButton
            // 
            okButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            okButton.DialogResult = DialogResult.OK;
            okButton.Location = new Point(293, 11);
            okButton.Name = "okButton";
            okButton.Size = new Size(75, 25);
            okButton.TabIndex = 2;
            okButton.Text = "OK";
            okButton.UseVisualStyleBackColor = true;
            // 
            // cancelButton
            // 
            cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cancelButton.Enabled = false;
            cancelButton.Location = new Point(202, 11);
            cancelButton.Name = "cancelButton";
            cancelButton.Size = new Size(80, 25);
            cancelButton.TabIndex = 1;
            cancelButton.Text = "Cancel";
            cancelButton.UseVisualStyleBackColor = true;
            // 
            // establishedContentPanel
            // 
            establishedContentPanel.BackColor = Color.White;
            establishedContentPanel.Controls.Add(establishedFooterPanel);
            establishedContentPanel.Controls.Add(mainInstructionLabel);
            establishedContentPanel.Controls.Add(establishedBodyLabel);
            establishedContentPanel.Controls.Add(establishedIcon);
            establishedContentPanel.Dock = DockStyle.Fill;
            establishedContentPanel.Location = new Point(0, 0);
            establishedContentPanel.Margin = new Padding(0);
            establishedContentPanel.Name = "establishedContentPanel";
            establishedContentPanel.Size = new Size(384, 136);
            establishedContentPanel.TabIndex = 0;
            establishedContentPanel.Visible = false;
            //
            // InternetAccessForm
            //
            AcceptButton = okButton;
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(384, 136);
            Controls.Add(waitingPanel);
            Controls.Add(establishedContentPanel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "InternetAccessForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Elkjøp - Setup Agent";
            TopMost = true;
            waitingPanel.ResumeLayout(false);
            waitingPanel.PerformLayout();
            panel1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)waitingIcon).EndInit();
            ((System.ComponentModel.ISupportInitialize)establishedIcon).EndInit();
            establishedFooterPanel.ResumeLayout(false);
            establishedContentPanel.ResumeLayout(false);
            establishedContentPanel.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.Panel waitingPanel;
        private System.Windows.Forms.Label waitingBodyLabel;
        private System.Windows.Forms.PictureBox waitingIcon;
        private System.Windows.Forms.Timer connectivityPollTimer;
        private System.Windows.Forms.Timer countdownTimer;
        private Label label1;
        private Panel panel1;
        private Panel panel2;
        private PictureBox establishedIcon;
        private Label establishedBodyLabel;
        private Label mainInstructionLabel;
        private Panel establishedFooterPanel;
        private Button cancelButton;
        private Button okButton;
        private Panel establishedSeparator;
        private Panel establishedContentPanel;
    }
}
