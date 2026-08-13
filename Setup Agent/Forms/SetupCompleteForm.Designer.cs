namespace Setup_Agent.Forms
{
    partial class SetupCompleteForm
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
            icon = new PictureBox();
            messageLabel = new Label();
            okButton = new Button();
            countdownTimer = new System.Windows.Forms.Timer(components);
            footerPanel = new Panel();
            separatorPanel = new Panel();
            contentPanel = new Panel();
            mainInstructionLabel = new Label();
            ((System.ComponentModel.ISupportInitialize)icon).BeginInit();
            footerPanel.SuspendLayout();
            contentPanel.SuspendLayout();
            SuspendLayout();
            //
            // icon
            //
            icon.Image = SystemIcons.Information.ToBitmap();
            icon.Location = new Point(20, 20);
            icon.Name = "icon";
            icon.Size = new Size(40, 40);
            icon.SizeMode = PictureBoxSizeMode.StretchImage;
            icon.TabIndex = 0;
            icon.TabStop = false;
            // 
            // messageLabel
            // 
            messageLabel.AutoSize = true;
            messageLabel.Location = new Point(80, 50);
            messageLabel.Name = "messageLabel";
            messageLabel.Size = new Size(232, 15);
            messageLabel.TabIndex = 2;
            messageLabel.Text = "Setup Agent is finishing up. Closing in 10...";
            // 
            // okButton
            //
            okButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            okButton.DialogResult = DialogResult.OK;
            okButton.Location = new Point(293, 11);
            okButton.Name = "okButton";
            okButton.Size = new Size(75, 25);
            okButton.TabIndex = 1;
            okButton.Text = "OK";
            okButton.UseVisualStyleBackColor = true;
            // 
            // countdownTimer
            // 
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += CountdownTimer_Tick;
            // 
            // footerPanel
            // 
            footerPanel.BackColor = Color.FromArgb(240, 240, 240);
            footerPanel.Controls.Add(okButton);
            footerPanel.Controls.Add(separatorPanel);
            footerPanel.Dock = DockStyle.Bottom;
            footerPanel.Location = new Point(0, 89);
            footerPanel.Margin = new Padding(0);
            footerPanel.Name = "footerPanel";
            footerPanel.Size = new Size(384, 47);
            footerPanel.TabIndex = 5;
            //
            // separatorPanel
            // 
            separatorPanel.BackColor = Color.FromArgb(223, 223, 223);
            separatorPanel.Dock = DockStyle.Top;
            separatorPanel.Location = new Point(0, 0);
            separatorPanel.Margin = new Padding(0);
            separatorPanel.Name = "separatorPanel";
            separatorPanel.Size = new Size(384, 1);
            separatorPanel.TabIndex = 0;
            // 
            // contentPanel
            // 
            contentPanel.BackColor = Color.White;
            contentPanel.Controls.Add(messageLabel);
            contentPanel.Controls.Add(mainInstructionLabel);
            contentPanel.Controls.Add(icon);
            contentPanel.Dock = DockStyle.Fill;
            contentPanel.Location = new Point(0, 0);
            contentPanel.Margin = new Padding(0);
            contentPanel.Name = "contentPanel";
            contentPanel.Size = new Size(384, 89);
            contentPanel.TabIndex = 6;
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
            mainInstructionLabel.Size = new Size(198, 21);
            mainInstructionLabel.TabIndex = 1;
            mainInstructionLabel.Text = "Windows is fully up to date";
            //
            // SetupCompleteForm
            //
            AcceptButton = okButton;
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(384, 136);
            Controls.Add(footerPanel);
            Controls.Add(contentPanel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "SetupCompleteForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Setup complete";
            TopMost = true;
            ((System.ComponentModel.ISupportInitialize)icon).EndInit();
            footerPanel.ResumeLayout(false);
            contentPanel.ResumeLayout(false);
            contentPanel.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.PictureBox icon;
        private System.Windows.Forms.Label messageLabel;
        private System.Windows.Forms.Button okButton;
        private System.Windows.Forms.Timer countdownTimer;
        private Panel footerPanel;
        private Panel separatorPanel;
        private Panel contentPanel;
        private Label mainInstructionLabel;
    }
}
