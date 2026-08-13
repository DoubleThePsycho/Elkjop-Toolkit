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
            this.components = new System.ComponentModel.Container();
            this.layoutPanel = new System.Windows.Forms.TableLayoutPanel();
            this.statusLabel = new System.Windows.Forms.Label();
            this.lastCheckedLabel = new System.Windows.Forms.Label();
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.updatesGrid = new System.Windows.Forms.DataGridView();
            this.nameColumn = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.stateColumn = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.layoutPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.updatesGrid)).BeginInit();
            this.SuspendLayout();
            //
            // layoutPanel
            //
            this.layoutPanel.ColumnCount = 1;
            this.layoutPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutPanel.Controls.Add(this.statusLabel, 0, 0);
            this.layoutPanel.Controls.Add(this.lastCheckedLabel, 0, 1);
            this.layoutPanel.Controls.Add(this.progressBar, 0, 2);
            this.layoutPanel.Controls.Add(this.updatesGrid, 0, 3);
            this.layoutPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.layoutPanel.Location = new System.Drawing.Point(0, 0);
            this.layoutPanel.Name = "layoutPanel";
            this.layoutPanel.Padding = new System.Windows.Forms.Padding(16);
            this.layoutPanel.RowCount = 4;
            this.layoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.layoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.layoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.layoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutPanel.Size = new System.Drawing.Size(460, 340);
            this.layoutPanel.TabIndex = 0;
            //
            // statusLabel
            //
            this.statusLabel.Dock = System.Windows.Forms.DockStyle.Top;
            this.statusLabel.Location = new System.Drawing.Point(16, 16);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(428, 24);
            this.statusLabel.TabIndex = 0;
            this.statusLabel.Text = "Starting Windows Update...";
            //
            // lastCheckedLabel
            //
            this.lastCheckedLabel.Dock = System.Windows.Forms.DockStyle.Top;
            this.lastCheckedLabel.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lastCheckedLabel.Location = new System.Drawing.Point(16, 40);
            this.lastCheckedLabel.Name = "lastCheckedLabel";
            this.lastCheckedLabel.Size = new System.Drawing.Size(428, 20);
            this.lastCheckedLabel.TabIndex = 1;
            //
            // progressBar
            //
            this.progressBar.Dock = System.Windows.Forms.DockStyle.Top;
            this.progressBar.Location = new System.Drawing.Point(16, 64);
            this.progressBar.MarqueeAnimationSpeed = 30;
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new System.Drawing.Size(428, 18);
            this.progressBar.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            this.progressBar.TabIndex = 2;
            //
            // updatesGrid
            //
            this.updatesGrid.AllowUserToAddRows = false;
            this.updatesGrid.AllowUserToDeleteRows = false;
            this.updatesGrid.AllowUserToResizeRows = false;
            this.updatesGrid.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
            this.updatesGrid.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            this.updatesGrid.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.nameColumn,
            this.stateColumn});
            this.updatesGrid.Dock = System.Windows.Forms.DockStyle.Fill;
            this.updatesGrid.Location = new System.Drawing.Point(16, 90);
            this.updatesGrid.Name = "updatesGrid";
            this.updatesGrid.ReadOnly = true;
            this.updatesGrid.RowHeadersVisible = false;
            this.updatesGrid.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.updatesGrid.Size = new System.Drawing.Size(428, 234);
            this.updatesGrid.TabIndex = 3;
            //
            // nameColumn
            //
            this.nameColumn.FillWeight = 70F;
            this.nameColumn.HeaderText = "Name";
            this.nameColumn.Name = "nameColumn";
            this.nameColumn.ReadOnly = true;
            //
            // stateColumn
            //
            this.stateColumn.FillWeight = 30F;
            this.stateColumn.HeaderText = "State";
            this.stateColumn.Name = "stateColumn";
            this.stateColumn.ReadOnly = true;
            //
            // UpdateProgressForm
            //
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(460, 340);
            this.Controls.Add(this.layoutPanel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "UpdateProgressForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Elkjøp - Setup Agent";
            this.TopMost = true;
            this.layoutPanel.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.updatesGrid)).EndInit();
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.TableLayoutPanel layoutPanel;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.Label lastCheckedLabel;
        private System.Windows.Forms.ProgressBar progressBar;
        private System.Windows.Forms.DataGridView updatesGrid;
        private System.Windows.Forms.DataGridViewTextBoxColumn nameColumn;
        private System.Windows.Forms.DataGridViewTextBoxColumn stateColumn;
    }
}
