namespace NLayerMp3ToWav;

partial class Form1
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
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
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        this.buttonConvert = new System.Windows.Forms.Button();
        this.SuspendLayout();
        // 
        // buttonConvert
        // 
        this.buttonConvert.Location = new System.Drawing.Point(50, 40);
        this.buttonConvert.Name = "buttonConvert";
        this.buttonConvert.Size = new System.Drawing.Size(180, 40);
        this.buttonConvert.TabIndex = 0;
        this.buttonConvert.Text = "Convert MP3 to WAV";
        this.buttonConvert.UseVisualStyleBackColor = true;
        this.buttonConvert.Click += new System.EventHandler(this.buttonConvert_Click);
        // 
        // Form1
        // 
        this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.ClientSize = new System.Drawing.Size(284, 121);
        this.Controls.Add(this.buttonConvert);
        this.Name = "Form1";
        this.Text = "NLayer MP3 to WAV";
        this.ResumeLayout(false);
    }

    #endregion

    private System.Windows.Forms.Button buttonConvert;
}
