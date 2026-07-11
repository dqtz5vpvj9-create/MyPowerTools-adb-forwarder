namespace AdbForwarder
{
    partial class Form1
    {
        /// <summary>
        /// 必需的设计器变量。
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// 清理所有正在使用的资源。
        /// </summary>
        /// <param name="disposing">如果应释放托管资源，为 true；否则为 false。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows 窗体设计器生成的代码

        /// <summary>
        /// 设计器支持所需的方法 - 不要修改
        /// 使用代码编辑器修改此方法的内容。
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            this.notifyIcon1 = new System.Windows.Forms.NotifyIcon(this.components);
            this.mainSplitContainer = new System.Windows.Forms.SplitContainer();
            this.topSplitContainer = new System.Windows.Forms.SplitContainer();
            this.forwardGroupBox = new System.Windows.Forms.GroupBox();
            this.forwardDeviceListView = new System.Windows.Forms.ListView();
            this.colForwardDeviceId = new System.Windows.Forms.ColumnHeader();
            this.colForwardPort = new System.Windows.Forms.ColumnHeader();
            this.colForwardStatus = new System.Windows.Forms.ColumnHeader();
            this.colForwardLastSeen = new System.Windows.Forms.ColumnHeader();
            this.wifiAdbGroupBox = new System.Windows.Forms.GroupBox();
            this.wifiAdbListView = new System.Windows.Forms.ListView();
            this.colWifiName = new System.Windows.Forms.ColumnHeader();
            this.colWifiEndpoint = new System.Windows.Forms.ColumnHeader();
            this.colWifiUsbSerial = new System.Windows.Forms.ColumnHeader();
            this.colWifiStatus = new System.Windows.Forms.ColumnHeader();
            this.colWifiLastAction = new System.Windows.Forms.ColumnHeader();
            this.richTextBox1 = new System.Windows.Forms.RichTextBox();
            this.statusStrip1 = new System.Windows.Forms.StatusStrip();
            this.lblAdminStatus = new System.Windows.Forms.ToolStripStatusLabel();
            this.lblDeviceCount = new System.Windows.Forms.ToolStripStatusLabel();
            this.lblWifiDeviceCount = new System.Windows.Forms.ToolStripStatusLabel();
            this.btnEditConfig = new System.Windows.Forms.ToolStripStatusLabel();
            this.btnOpenConfigDir = new System.Windows.Forms.ToolStripStatusLabel();
            this.btnReloadConfig = new System.Windows.Forms.ToolStripStatusLabel();
            this.btnClearLog = new System.Windows.Forms.ToolStripStatusLabel();
            ((System.ComponentModel.ISupportInitialize)(this.mainSplitContainer)).BeginInit();
            this.mainSplitContainer.Panel1.SuspendLayout();
            this.mainSplitContainer.Panel2.SuspendLayout();
            this.mainSplitContainer.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.topSplitContainer)).BeginInit();
            this.topSplitContainer.Panel1.SuspendLayout();
            this.topSplitContainer.Panel2.SuspendLayout();
            this.topSplitContainer.SuspendLayout();
            this.forwardGroupBox.SuspendLayout();
            this.wifiAdbGroupBox.SuspendLayout();
            this.statusStrip1.SuspendLayout();
            this.SuspendLayout();
            // 
            // notifyIcon1
            // 
            this.notifyIcon1.Icon = ((System.Drawing.Icon)(resources.GetObject("notifyIcon1.Icon")));
            this.notifyIcon1.Text = "ADB Forwarder";
            this.notifyIcon1.Visible = true;
            // 
            // mainSplitContainer
            // 
            this.mainSplitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.mainSplitContainer.Location = new System.Drawing.Point(0, 0);
            this.mainSplitContainer.Name = "mainSplitContainer";
            this.mainSplitContainer.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // mainSplitContainer.Panel1
            // 
            this.mainSplitContainer.Panel1.Controls.Add(this.topSplitContainer);
            this.mainSplitContainer.Panel1MinSize = 180;
            // 
            // mainSplitContainer.Panel2
            // 
            this.mainSplitContainer.Panel2.Controls.Add(this.richTextBox1);
            this.mainSplitContainer.Size = new System.Drawing.Size(1184, 641);
            this.mainSplitContainer.SplitterDistance = 260;
            this.mainSplitContainer.TabIndex = 0;
            // 
            // topSplitContainer
            // 
            this.topSplitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.topSplitContainer.Location = new System.Drawing.Point(0, 0);
            this.topSplitContainer.Name = "topSplitContainer";
            // 
            // topSplitContainer.Panel1
            // 
            this.topSplitContainer.Panel1.Controls.Add(this.forwardGroupBox);
            this.topSplitContainer.Panel1MinSize = 420;
            // 
            // topSplitContainer.Panel2
            // 
            this.topSplitContainer.Panel2.Controls.Add(this.wifiAdbGroupBox);
            this.topSplitContainer.Panel2MinSize = 420;
            this.topSplitContainer.Size = new System.Drawing.Size(1184, 260);
            this.topSplitContainer.SplitterDistance = 570;
            this.topSplitContainer.TabIndex = 0;
            // 
            // forwardGroupBox
            // 
            this.forwardGroupBox.Controls.Add(this.forwardDeviceListView);
            this.forwardGroupBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.forwardGroupBox.Location = new System.Drawing.Point(0, 0);
            this.forwardGroupBox.Name = "forwardGroupBox";
            this.forwardGroupBox.Size = new System.Drawing.Size(570, 260);
            this.forwardGroupBox.TabIndex = 0;
            this.forwardGroupBox.TabStop = false;
            this.forwardGroupBox.Text = "Forward Devices";
            // 
            // forwardDeviceListView
            // 
            this.forwardDeviceListView.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.colForwardDeviceId,
            this.colForwardPort,
            this.colForwardStatus,
            this.colForwardLastSeen});
            this.forwardDeviceListView.Dock = System.Windows.Forms.DockStyle.Fill;
            this.forwardDeviceListView.FullRowSelect = true;
            this.forwardDeviceListView.GridLines = true;
            this.forwardDeviceListView.HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.Nonclickable;
            this.forwardDeviceListView.HideSelection = false;
            this.forwardDeviceListView.Location = new System.Drawing.Point(3, 24);
            this.forwardDeviceListView.Name = "forwardDeviceListView";
            this.forwardDeviceListView.Size = new System.Drawing.Size(564, 233);
            this.forwardDeviceListView.TabIndex = 0;
            this.forwardDeviceListView.UseCompatibleStateImageBehavior = false;
            this.forwardDeviceListView.View = System.Windows.Forms.View.Details;
            // 
            // colForwardDeviceId
            // 
            this.colForwardDeviceId.Text = "Device ID";
            this.colForwardDeviceId.Width = 190;
            // 
            // colForwardPort
            // 
            this.colForwardPort.Text = "Port";
            this.colForwardPort.Width = 70;
            // 
            // colForwardStatus
            // 
            this.colForwardStatus.Text = "Status";
            this.colForwardStatus.Width = 110;
            // 
            // colForwardLastSeen
            // 
            this.colForwardLastSeen.Text = "Last Seen Online";
            this.colForwardLastSeen.Width = 160;
            // 
            // wifiAdbGroupBox
            // 
            this.wifiAdbGroupBox.Controls.Add(this.wifiAdbListView);
            this.wifiAdbGroupBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.wifiAdbGroupBox.Location = new System.Drawing.Point(0, 0);
            this.wifiAdbGroupBox.Name = "wifiAdbGroupBox";
            this.wifiAdbGroupBox.Size = new System.Drawing.Size(610, 260);
            this.wifiAdbGroupBox.TabIndex = 0;
            this.wifiAdbGroupBox.TabStop = false;
            this.wifiAdbGroupBox.Text = "Wi-Fi ADB Devices";
            // 
            // wifiAdbListView
            // 
            this.wifiAdbListView.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.colWifiName,
            this.colWifiEndpoint,
            this.colWifiUsbSerial,
            this.colWifiStatus,
            this.colWifiLastAction});
            this.wifiAdbListView.Dock = System.Windows.Forms.DockStyle.Fill;
            this.wifiAdbListView.FullRowSelect = true;
            this.wifiAdbListView.GridLines = true;
            this.wifiAdbListView.HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.Nonclickable;
            this.wifiAdbListView.HideSelection = false;
            this.wifiAdbListView.Location = new System.Drawing.Point(3, 24);
            this.wifiAdbListView.Name = "wifiAdbListView";
            this.wifiAdbListView.Size = new System.Drawing.Size(604, 233);
            this.wifiAdbListView.TabIndex = 0;
            this.wifiAdbListView.UseCompatibleStateImageBehavior = false;
            this.wifiAdbListView.View = System.Windows.Forms.View.Details;
            // 
            // colWifiName
            // 
            this.colWifiName.Text = "Name";
            this.colWifiName.Width = 110;
            // 
            // colWifiEndpoint
            // 
            this.colWifiEndpoint.Text = "Endpoint";
            this.colWifiEndpoint.Width = 120;
            // 
            // colWifiUsbSerial
            // 
            this.colWifiUsbSerial.Text = "USB Serial";
            this.colWifiUsbSerial.Width = 140;
            // 
            // colWifiStatus
            // 
            this.colWifiStatus.Text = "Status";
            this.colWifiStatus.Width = 110;
            // 
            // colWifiLastAction
            // 
            this.colWifiLastAction.Text = "Last Action";
            this.colWifiLastAction.Width = 180;
            // 
            // richTextBox1
            // 
            this.richTextBox1.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(30)))), ((int)(((byte)(30)))), ((int)(((byte)(30)))));
            this.richTextBox1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.richTextBox1.Font = new System.Drawing.Font("Consolas", 9F);
            this.richTextBox1.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(220)))), ((int)(((byte)(220)))), ((int)(((byte)(220)))));
            this.richTextBox1.Location = new System.Drawing.Point(0, 0);
            this.richTextBox1.Name = "richTextBox1";
            this.richTextBox1.ReadOnly = true;
            this.richTextBox1.Size = new System.Drawing.Size(1184, 377);
            this.richTextBox1.TabIndex = 0;
            this.richTextBox1.Text = "";
            // 
            // statusStrip1
            // 
            this.statusStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.lblAdminStatus,
            this.lblDeviceCount,
            this.lblWifiDeviceCount,
            this.btnEditConfig,
            this.btnOpenConfigDir,
            this.btnReloadConfig,
            this.btnClearLog});
            this.statusStrip1.Location = new System.Drawing.Point(0, 641);
            this.statusStrip1.Name = "statusStrip1";
            this.statusStrip1.Size = new System.Drawing.Size(1184, 22);
            this.statusStrip1.TabIndex = 1;
            // 
            // lblAdminStatus
            // 
            this.lblAdminStatus.Name = "lblAdminStatus";
            this.lblAdminStatus.Size = new System.Drawing.Size(0, 17);
            // 
            // lblDeviceCount
            // 
            this.lblDeviceCount.Name = "lblDeviceCount";
            this.lblDeviceCount.Size = new System.Drawing.Size(0, 17);
            // 
            // lblWifiDeviceCount
            // 
            this.lblWifiDeviceCount.Name = "lblWifiDeviceCount";
            this.lblWifiDeviceCount.Size = new System.Drawing.Size(0, 17);
            // 
            // btnEditConfig
            // 
            this.btnEditConfig.IsLink = true;
            this.btnEditConfig.Name = "btnEditConfig";
            this.btnEditConfig.Size = new System.Drawing.Size(71, 17);
            this.btnEditConfig.Text = "Edit Config";
            this.btnEditConfig.Click += new System.EventHandler(this.btnEditConfig_Click);
            // 
            // btnOpenConfigDir
            // 
            this.btnOpenConfigDir.IsLink = true;
            this.btnOpenConfigDir.Name = "btnOpenConfigDir";
            this.btnOpenConfigDir.Size = new System.Drawing.Size(103, 17);
            this.btnOpenConfigDir.Text = "Open Config Dir";
            this.btnOpenConfigDir.Click += new System.EventHandler(this.btnOpenConfigDir_Click);
            // 
            // btnReloadConfig
            // 
            this.btnReloadConfig.IsLink = true;
            this.btnReloadConfig.Name = "btnReloadConfig";
            this.btnReloadConfig.Size = new System.Drawing.Size(86, 17);
            this.btnReloadConfig.Text = "Reload Config";
            this.btnReloadConfig.Click += new System.EventHandler(this.btnReloadConfig_Click);
            // 
            // btnClearLog
            // 
            this.btnClearLog.IsLink = true;
            this.btnClearLog.Name = "btnClearLog";
            this.btnClearLog.Size = new System.Drawing.Size(60, 17);
            this.btnClearLog.Spring = true;
            this.btnClearLog.Text = "Clear Log";
            this.btnClearLog.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            this.btnClearLog.Click += new System.EventHandler(this.btnClearLog_Click);
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 18F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1184, 663);
            this.Controls.Add(this.mainSplitContainer);
            this.Controls.Add(this.statusStrip1);
            this.MinimumSize = new System.Drawing.Size(1000, 600);
            this.Name = "Form1";
            this.Text = "ADB Forwarder";
            this.Load += new System.EventHandler(this.Form1_Load);
            this.mainSplitContainer.Panel1.ResumeLayout(false);
            this.mainSplitContainer.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.mainSplitContainer)).EndInit();
            this.mainSplitContainer.ResumeLayout(false);
            this.topSplitContainer.Panel1.ResumeLayout(false);
            this.topSplitContainer.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.topSplitContainer)).EndInit();
            this.topSplitContainer.ResumeLayout(false);
            this.forwardGroupBox.ResumeLayout(false);
            this.wifiAdbGroupBox.ResumeLayout(false);
            this.statusStrip1.ResumeLayout(false);
            this.statusStrip1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private System.Windows.Forms.NotifyIcon notifyIcon1;
        private System.Windows.Forms.SplitContainer mainSplitContainer;
        private System.Windows.Forms.SplitContainer topSplitContainer;
        private System.Windows.Forms.GroupBox forwardGroupBox;
        private System.Windows.Forms.ListView forwardDeviceListView;
        private System.Windows.Forms.ColumnHeader colForwardDeviceId;
        private System.Windows.Forms.ColumnHeader colForwardPort;
        private System.Windows.Forms.ColumnHeader colForwardStatus;
        private System.Windows.Forms.ColumnHeader colForwardLastSeen;
        private System.Windows.Forms.GroupBox wifiAdbGroupBox;
        private System.Windows.Forms.ListView wifiAdbListView;
        private System.Windows.Forms.ColumnHeader colWifiName;
        private System.Windows.Forms.ColumnHeader colWifiEndpoint;
        private System.Windows.Forms.ColumnHeader colWifiUsbSerial;
        private System.Windows.Forms.ColumnHeader colWifiStatus;
        private System.Windows.Forms.ColumnHeader colWifiLastAction;
        private System.Windows.Forms.RichTextBox richTextBox1;
        private System.Windows.Forms.StatusStrip statusStrip1;
        private System.Windows.Forms.ToolStripStatusLabel lblAdminStatus;
        private System.Windows.Forms.ToolStripStatusLabel lblDeviceCount;
        private System.Windows.Forms.ToolStripStatusLabel lblWifiDeviceCount;
        private System.Windows.Forms.ToolStripStatusLabel btnEditConfig;
        private System.Windows.Forms.ToolStripStatusLabel btnOpenConfigDir;
        private System.Windows.Forms.ToolStripStatusLabel btnReloadConfig;
        private System.Windows.Forms.ToolStripStatusLabel btnClearLog;
    }
}
