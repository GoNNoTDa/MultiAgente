namespace MultiAgente.App;

partial class VentanaPrincipal
{
    private System.ComponentModel.IContainer components = null!;
    private Microsoft.Web.WebView2.WinForms.WebView2 web = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        web = new Microsoft.Web.WebView2.WinForms.WebView2();
        ((System.ComponentModel.ISupportInitialize)web).BeginInit();
        SuspendLayout();
        //
        // web
        //
        web.AllowExternalDrop = true;
        web.Dock = DockStyle.Fill;
        web.Location = new Point(0, 0);
        web.Name = "web";
        web.Size = new Size(1264, 761);
        web.TabIndex = 0;
        web.ZoomFactor = 1D;
        //
        // VentanaPrincipal
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1264, 761);
        Controls.Add(web);
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(820, 560);
        Name = "VentanaPrincipal";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "MultiAgente";
        FormClosing += VentanaPrincipal_FormClosing;
        Load += VentanaPrincipal_Load;
        ((System.ComponentModel.ISupportInitialize)web).EndInit();
        ResumeLayout(false);
    }
}
