namespace Setup_Agent.Forms
{
    partial class UpdateProgressForm
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
            layoutPanel = new TableLayoutPanel();
            statusLabel = new Label();
            lastCheckedRowPanel = new Panel();
            preventSleepCheckBox = new CheckBox();
            lastCheckedLabel = new Label();
            progressBar = new ProgressBar();
            updatesGrid = new DataGridView();
            nameColumn = new DataGridViewTextBoxColumn();
            percentColumn = new DataGridViewTextBoxColumn();
            stateColumn = new DataGridViewTextBoxColumn();
            heartbeatTimer = new System.Windows.Forms.Timer(components);
            layoutPanel.SuspendLayout();
            lastCheckedRowPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)updatesGrid).BeginInit();
            SuspendLayout();
            // 
            // layoutPanel
            // 
            layoutPanel.ColumnCount = 1;
            layoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layoutPanel.Controls.Add(statusLabel, 0, 0);
            layoutPanel.Controls.Add(lastCheckedRowPanel, 0, 1);
            layoutPanel.Controls.Add(progressBar, 0, 2);
            layoutPanel.Controls.Add(updatesGrid, 0, 3);
            layoutPanel.Dock = DockStyle.Fill;
            layoutPanel.Location = new Point(0, 0);
            layoutPanel.Name = "layoutPanel";
            layoutPanel.Padding = new Padding(16);
            layoutPanel.RowCount = 4;
            layoutPanel.RowStyles.Add(new RowStyle());
            layoutPanel.RowStyles.Add(new RowStyle());
            layoutPanel.RowStyles.Add(new RowStyle());
            layoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layoutPanel.Size = new Size(460, 340);
            layoutPanel.TabIndex = 0;
            // 
            // statusLabel
            // 
            statusLabel.Dock = DockStyle.Top;
            statusLabel.Location = new Point(19, 16);
            statusLabel.Name = "statusLabel";
            statusLabel.Size = new Size(422, 24);
            statusLabel.TabIndex = 0;
            statusLabel.Text = "Starting Windows Update...";
            // 
            // lastCheckedRowPanel
            // 
            lastCheckedRowPanel.AutoSize = true;
            lastCheckedRowPanel.Controls.Add(preventSleepCheckBox);
            lastCheckedRowPanel.Controls.Add(lastCheckedLabel);
            lastCheckedRowPanel.Dock = DockStyle.Top;
            lastCheckedRowPanel.Location = new Point(16, 40);
            lastCheckedRowPanel.Margin = new Padding(0);
            lastCheckedRowPanel.Name = "lastCheckedRowPanel";
            lastCheckedRowPanel.Size = new Size(428, 18);
            lastCheckedRowPanel.TabIndex = 1;
            // 
            // preventSleepCheckBox
            // 
            preventSleepCheckBox.AutoSize = true;
            preventSleepCheckBox.Checked = true;
            preventSleepCheckBox.CheckState = CheckState.Checked;
            preventSleepCheckBox.Dock = DockStyle.Right;
            preventSleepCheckBox.Location = new Point(250, 0);
            preventSleepCheckBox.Name = "preventSleepCheckBox";
            preventSleepCheckBox.Size = new Size(178, 18);
            preventSleepCheckBox.TabIndex = 1;
            preventSleepCheckBox.Text = "Prevent sleep while updating";
            preventSleepCheckBox.UseVisualStyleBackColor = true;
            preventSleepCheckBox.CheckedChanged += PreventSleepCheckBox_CheckedChanged;
            // 
            // lastCheckedLabel
            // 
            lastCheckedLabel.AutoSize = true;
            lastCheckedLabel.ForeColor = SystemColors.GrayText;
            lastCheckedLabel.Location = new Point(0, 3);
            lastCheckedLabel.Name = "lastCheckedLabel";
            lastCheckedLabel.Size = new Size(0, 15);
            lastCheckedLabel.TabIndex = 0;
            lastCheckedLabel.Text = "Last checked: never";
            // 
            // progressBar
            // 
            progressBar.Dock = DockStyle.Top;
            progressBar.Location = new Point(19, 61);
            progressBar.MarqueeAnimationSpeed = 30;
            progressBar.Name = "progressBar";
            progressBar.Size = new Size(422, 18);
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.TabIndex = 2;
            // 
            // updatesGrid
            // 
            updatesGrid.AllowUserToAddRows = false;
            updatesGrid.AllowUserToDeleteRows = false;
            updatesGrid.AllowUserToResizeRows = false;
            updatesGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            updatesGrid.BorderStyle = BorderStyle.None;
            updatesGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            updatesGrid.Columns.AddRange(new DataGridViewColumn[] { nameColumn, percentColumn, stateColumn });
            updatesGrid.Dock = DockStyle.Fill;
            updatesGrid.Location = new Point(19, 85);
            updatesGrid.Name = "updatesGrid";
            updatesGrid.ReadOnly = true;
            updatesGrid.RowHeadersVisible = false;
            updatesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            updatesGrid.Size = new Size(422, 236);
            updatesGrid.TabIndex = 3;
            // 
            // nameColumn
            // 
            nameColumn.FillWeight = 55F;
            nameColumn.HeaderText = "Name";
            nameColumn.Name = "nameColumn";
            nameColumn.ReadOnly = true;
            // 
            // percentColumn
            // 
            percentColumn.FillWeight = 15F;
            percentColumn.HeaderText = "Progress";
            percentColumn.Name = "percentColumn";
            percentColumn.ReadOnly = true;
            // 
            // stateColumn
            // 
            stateColumn.FillWeight = 20F;
            stateColumn.HeaderText = "State";
            stateColumn.Name = "stateColumn";
            stateColumn.ReadOnly = true;
            // 
            // heartbeatTimer
            // 
            heartbeatTimer.Interval = 7000;
            heartbeatTimer.Tick += HeartbeatTimer_Tick;
            // 
            // UpdateProgressForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(460, 340);
            Controls.Add(layoutPanel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "UpdateProgressForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Elkjøp - Setup Agent";
            TopMost = true;
            layoutPanel.ResumeLayout(false);
            layoutPanel.PerformLayout();
            lastCheckedRowPanel.ResumeLayout(false);
            lastCheckedRowPanel.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)updatesGrid).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.TableLayoutPanel layoutPanel;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.Panel lastCheckedRowPanel;
        private System.Windows.Forms.CheckBox preventSleepCheckBox;
        private System.Windows.Forms.Label lastCheckedLabel;
        private System.Windows.Forms.ProgressBar progressBar;
        private System.Windows.Forms.DataGridView updatesGrid;
        private System.Windows.Forms.Timer heartbeatTimer;
        private DataGridViewTextBoxColumn nameColumn;
        private DataGridViewTextBoxColumn percentColumn;
        private DataGridViewTextBoxColumn stateColumn;
    }
}
