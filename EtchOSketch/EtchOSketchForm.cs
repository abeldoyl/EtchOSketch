/* 
Abel Doyle
RCET3371
BetterEtchOSketch
https://github.com/abeldoyl/RECT-3371-Assignments/tree/main/EtchOSketch
*/
using System.IO.Ports;
using System.Media;
namespace EtchOSketch
{
    public partial class EtchOSketchForm : Form
    {
        public EtchOSketchForm()
        {
            InitializeComponent();

            // The drawing lives in a Bitmap so it survives window repaints
            canvas = new Bitmap(DisplayPictureBox.ClientSize.Width, DisplayPictureBox.ClientSize.Height);
            ClearCanvas();
            DisplayPictureBox.Image = canvas;

            MouseRadioButton.Checked = true;       // start in mouse mode

            StatusTimer.Interval = 10;             // do NOT also double-click the timer in the designer
            StatusTimer.Tick += StatusTimer_Tick;
            StatusTimer.Start();
        }

        // ---------- drawing state ----------
        Bitmap canvas;
        int oldX = 0, oldY = 0;
        Color penColor = Color.Black;
        ColorDialog penColorDialog = new ColorDialog();
        SoundPlayer? shakerPlayer;
        bool shaking = false;

        // ---------- mode / PIC pot state ----------
        bool externalMode = false;         // false = mouse, true = PIC pots
        bool havePenPos = false;           // true after the first pot position is known
        int potX = 0, potY = 0;            // latest ADC counts (0 to 1023)
        int nextChannel = 0;               // 0 = AN0 (X pot), 2 = AN2 (Y pot)

        // ---------- serial / connection state (from Lab 8) ----------
        const byte AdcChannelX = 0;
        const byte AdcChannelY = 2;
        byte[] idRequest = { 0x24, 0x49, 0x00 };
        byte[] idReply = { 0x24, 0x49, 0x02, 0x33, 0x75 };   // the only reply accepted as the PIC

        SerialPort _serialPort = new SerialPort();
        byte[] rxBuffer = new byte[10];
        int rxCount = 0;
        bool waiting = false;
        DateTime deadline;

        bool connected = false;
        bool commError = false;
        string currentPort = "none";
        DateTime nextPoll = DateTime.Now;      // used in mouse mode as a 1 second "is the PIC alive" check
        string statusNote = "";                // last event shown on the status label

        string[] scanPorts = new string[0];
        int scanIndex = 0;
        bool scanning = false;
        bool probing = false;
        DateTime nextScan = DateTime.Now;

        // =====================================================================
        // Canvas drawing
        // =====================================================================
        void ClearCanvas()
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.White);
            }
            DisplayPictureBox.Invalidate();
        }

        void UpdatePenColor()
        {
            penColorDialog.Color = penColor;
            if (penColorDialog.ShowDialog() == DialogResult.OK)
            {
                penColor = penColorDialog.Color;
            }
        }

        // Draw from (oldX, oldY) to the new point in the current pen color
        void DrawLineSegment(int newX, int newY)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            using (Pen thePen = new Pen(penColor, 2))
            {
                g.DrawLine(thePen, oldX, oldY, newX, newY);
            }
            DisplayPictureBox.Invalidate();
        }

        // 10 x 10 scope graticule
        void DrawGrid()
        {
            int w = canvas.Width - 1;
            int h = canvas.Height - 1;
            using (Graphics g = Graphics.FromImage(canvas))
            using (Pen gridPen = new Pen(Color.Gray, 1))
            {
                for (int i = 0; i <= 10; i++)
                {
                    int x = i * w / 10;
                    int y = i * h / 10;
                    g.DrawLine(gridPen, x, 0, x, h);   // vertical line
                    g.DrawLine(gridPen, 0, y, w, y);   // horizontal line
                }
            }
        }

        // One cycle (0 to 360 degrees) across the full width, +/-1 fills the full height
        void DrawWaveform(Func<double, double> waveFunction, Color waveColor)
        {
            int w = canvas.Width - 1;
            int h = canvas.Height - 1;
            PointF? lastPoint = null;
            using (Graphics g = Graphics.FromImage(canvas))
            using (Pen wavePen = new Pen(waveColor, 2))
            {
                for (int degrees = 0; degrees <= 360; degrees++)
                {
                    double value = waveFunction(degrees * Math.PI / 180);
                    if (double.IsNaN(value) || Math.Abs(value) > 1)
                    {
                        lastPoint = null;              // off the screen (tangent): break the line
                        continue;
                    }
                    PointF point = new PointF(degrees * w / 360f, (float)(h / 2.0 - value * h / 2.0));
                    if (lastPoint != null)
                    {
                        g.DrawLine(wavePen, lastPoint.Value, point);
                    }
                    lastPoint = point;
                }
            }
        }

        void DrawWaveforms()
        {
            ClearCanvas();
            DrawGrid();
            DrawWaveform(Math.Sin, Color.OrangeRed);
            DrawWaveform(Math.Cos, Color.Green);
            DrawWaveform(Math.Tan, Color.Purple);
            DisplayPictureBox.Invalidate();
            havePenPos = false;                        // restart the PIC pen after a redraw
        }

        // Shake the picture box, play the sound, then erase
        async void ClearWithShake()
        {
            if (shaking) return;
            shaking = true;
            try
            {
                Random random = new Random();
                Point original = DisplayPictureBox.Location;
                for (int i = 0; i < 12; i++)
                {
                    DisplayPictureBox.Location = new Point(original.X + random.Next(-8, 9), original.Y + random.Next(-8, 9));
                    await Task.Delay(40);              // keeps the form responsive, unlike Thread.Sleep
                }
                DisplayPictureBox.Location = original;
                ClearCanvas();
                havePenPos = false;
            }
            finally
            {
                shaking = false;
            }
        }

        // Turn the latest pot readings into a pen position and draw
        void DrawFromPots()
        {
            // Flip (1023 - potY) here if the Y pot feels upside down
            int x = Math.Clamp(potX * (canvas.Width - 1) / 1023, 0, canvas.Width - 1);
            int y = Math.Clamp(potY * (canvas.Height - 1) / 1023, 0, canvas.Height - 1);

            if (havePenPos == false)
            {
                oldX = x;                              // first sample: just place the pen
                oldY = y;
                havePenPos = true;
                return;
            }
            if (x != oldX || y != oldY)
            {
                DrawLineSegment(x, y);
                oldX = x;
                oldY = y;
            }
        }

        // =====================================================================
        // Serial communication (Lab 8 code, trimmed for the pots)
        // =====================================================================
        void SerialPortSetup(string portName)
        {
            _serialPort.Close();
            _serialPort = new SerialPort();
            _serialPort.PortName = portName;
            _serialPort.BaudRate = 9600;
            _serialPort.DataBits = 8;
            _serialPort.Parity = Parity.None;
            _serialPort.StopBits = StopBits.One;
        }

        void CloseQuietly()
        {
            try { _serialPort.Close(); }
            catch (Exception) { }                      // an unplugged adapter can throw here
        }

        string[] GetSerialPorts()
        {
            try { return SerialPort.GetPortNames(); }
            catch (Exception) { return new string[0]; }
        }

        // Send an ADC request for the next pot channel
        void RequestPot()
        {
            if (connected == false || _serialPort.IsOpen == false || waiting == true)
            {
                return;
            }
            byte[] adcRequest = { 0x24, 0x41, 0x01, (byte)nextChannel };
            _serialPort.DiscardInBuffer();
            rxCount = 0;
            _serialPort.Write(adcRequest, 0, adcRequest.Length);
            waiting = true;
            deadline = DateTime.Now.AddMilliseconds(500);
        }

        // A full packet arrived: ADC reply = 24 41 03 CH HH LL, error reply = 24 45 02 code detail
        void HandlePacket(int total)
        {
            if (rxBuffer[1] == 0x41 && total == 6)
            {
                int channel = rxBuffer[3];
                int adcValue = (rxBuffer[4] & 0x03) * 256 + rxBuffer[5];   // 10-bit value, 0 to 1023
                if (channel == AdcChannelX)
                {
                    potX = adcValue;
                    nextChannel = AdcChannelY;         // ask for Y next
                }
                else if (channel == AdcChannelY)
                {
                    potY = adcValue;
                    nextChannel = AdcChannelX;         // ask for X next
                    if (externalMode == true)
                    {
                        DrawFromPots();                // X and Y are both fresh now
                    }
                }
            }
            else if (rxBuffer[1] == 0x45)
            {
                statusNote = "PIC error code " + rxBuffer[3];
            }
        }

        void RemoveBytes(int n)
        {
            for (int i = 0; i < rxCount - n; i++)
            {
                rxBuffer[i] = rxBuffer[i + n];
            }
            rxCount = rxCount - n;
        }

        void ReadNewBytes()
        {
            while (_serialPort.BytesToRead > 0 && rxCount < rxBuffer.Length)
            {
                rxBuffer[rxCount] = (byte)_serialPort.ReadByte();
                rxCount++;
            }
        }

        // Total packet length = 3 + the LENGTH byte (byte 2)
        void CheckPackets()
        {
            while (true)
            {
                while (rxCount > 0 && rxBuffer[0] != 0x24)
                {
                    RemoveBytes(1);                    // throw away bytes before the '$'
                }
                if (rxCount < 3)
                {
                    return;
                }
                int total = 3 + rxBuffer[2];
                if (total > rxBuffer.Length)
                {
                    RemoveBytes(1);                    // impossible length: resync
                    continue;
                }
                if (rxCount < total)
                {
                    return;                            // rest of the packet is still coming
                }
                HandlePacket(total);
                waiting = false;
                RemoveBytes(total);
            }
        }

        // Runs every 10 ms and never blocks
        private void StatusTimer_Tick(object? sender, EventArgs e)
        {
            if (connected == true)
            {
                try
                {
                    RunConnected();
                }
                catch (Exception ex)
                {
                    ConnectionLost(ex.Message);
                }
            }
            else
            {
                RunDiscovery();
            }
            UpdateStatus();
        }

        void RunConnected()
        {
            if (_serialPort.IsOpen == false)
            {
                throw new Exception("port closed");
            }

            ReadNewBytes();
            CheckPackets();

            if (waiting == true && DateTime.Now > deadline)
            {
                throw new Exception("no ADC reply");   // the link is dead
            }

            if (waiting == false)
            {
                // External mode asks for pots nonstop; mouse mode only checks once a second
                if (externalMode == true || DateTime.Now >= nextPoll)
                {
                    nextPoll = DateTime.Now.AddMilliseconds(1000);
                    if (Array.IndexOf(GetSerialPorts(), currentPort) < 0)
                    {
                        throw new Exception(currentPort + " disappeared");
                    }
                    RequestPot();
                }
            }
        }

        // Not connected: test one serial port at a time with an ID request
        void RunDiscovery()
        {
            if (probing == false)
            {
                if (scanning == false)
                {
                    if (DateTime.Now < nextScan)
                    {
                        return;
                    }
                    scanPorts = GetSerialPorts();
                    scanIndex = 0;
                    scanning = true;
                    currentPort = "none";
                }
                if (scanIndex >= scanPorts.Length)
                {
                    scanning = false;
                    nextScan = DateTime.Now.AddMilliseconds(1000);
                    return;
                }
                StartProbe(scanPorts[scanIndex]);
                return;
            }

            try
            {
                ReadNewBytes();
            }
            catch (Exception)
            {
                rxCount = 0;                           // port went away mid-probe; FinishProbe will reject it
            }
            if (DateTime.Now < deadline && rxCount < rxBuffer.Length)
            {
                return;
            }
            FinishProbe();
        }

        void StartProbe(string name)
        {
            currentPort = name;
            try
            {
                SerialPortSetup(name);
                _serialPort.Open();
                _serialPort.DiscardInBuffer();
                rxCount = 0;
                _serialPort.Write(idRequest, 0, idRequest.Length);
                deadline = DateTime.Now.AddMilliseconds(300);
                probing = true;
            }
            catch (Exception)
            {
                CloseQuietly();                        // busy or invalid port: skip it
                scanIndex++;
            }
        }

        // Accept the port only if the complete reply is exactly 24 49 02 33 75
        void FinishProbe()
        {
            probing = false;

            bool ok = (rxCount == idReply.Length);
            for (int i = 0; i < idReply.Length && ok == true; i++)
            {
                if (rxBuffer[i] != idReply[i])
                {
                    ok = false;
                }
            }

            if (ok == true)
            {
                connected = true;
                commError = false;
                scanning = false;
                waiting = false;
                rxCount = 0;
                nextChannel = AdcChannelX;
                nextPoll = DateTime.Now;
                havePenPos = false;
                statusNote = "";
            }
            else
            {
                CloseQuietly();
                rxCount = 0;
                scanIndex++;
            }
        }

        // The port disappeared or the PIC stopped answering. Mouse mode keeps working.
        void ConnectionLost(string reason)
        {
            CloseQuietly();
            connected = false;
            commError = true;
            waiting = false;
            rxCount = 0;
            scanning = false;
            probing = false;
            havePenPos = false;
            nextScan = DateTime.Now.AddMilliseconds(1000);   // look for the PIC again in 1 second
            currentPort = "none";
            statusNote = "Connection lost: " + reason;
        }

        void UpdateStatus()
        {
            string state = "Searching for PIC...";
            if (connected == true) state = "PIC connected";
            else if (commError == true) state = "COM ERR - searching for PIC...";

            string mode = (externalMode == true) ? "External (PIC)" : "Mouse";
            if (externalMode == true && connected == false)
            {
                mode += " - no PIC, not drawing";
            }
            StatusLabel.Text = $"Mode: {mode}   Port: {currentPort}   {state}   {statusNote}";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StatusTimer.Stop();
            CloseQuietly();
            base.OnFormClosed(e);
        }

        // =====================================================================
        // Event handlers
        // =====================================================================
        private void DisplayPictureBox_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle)
            {
                UpdatePenColor();                      // middle click opens the dialog once
            }
            oldX = e.X;                                // so a new stroke starts where the mouse is
            oldY = e.Y;
        }

        private void DisplayPictureBox_MouseMove(object? sender, MouseEventArgs e)
        {
            if (externalMode == false && e.Button == MouseButtons.Left)
            {
                DrawLineSegment(e.X, e.Y);
            }
            oldX = e.X;
            oldY = e.Y;
        }


        private void MouseRadioButton_CheckedChanged(object? sender, EventArgs e)
        {
            if (MouseRadioButton.Checked == true)
            {
                externalMode = false;
            }
        }

        private void ExternalRadioButton_CheckedChanged(object? sender, EventArgs e)
        {
            if (ExternalRadioButton.Checked == true)
            {
                externalMode = true;
                havePenPos = false;                    // pen starts at the current pot position
            }
        }

        private void SelectColorButton_Click(object? sender, EventArgs e)
        {
            UpdatePenColor();
        }
        private void DrawWaveformsButton_Click(object? sender, EventArgs e)
        {
            DrawWaveforms();
        }
        private void ClearButton_Click(object? sender, EventArgs e)
        {
            ClearWithShake();
        }
        private void ExitButton_Click(object? sender, EventArgs e)
        {
            this.Close();
        }

        // Menu items and context menu items share these handlers
        private void exitToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            this.Close();
        }
        private void selectColorToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            UpdatePenColor();
        }
        private void drawWaveformsToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            DrawWaveforms();
        }
        private void clearToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            ClearWithShake();
        }

        private void aboutToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            MessageBox.Show("Better Etch-O-Sketch (PIC Edition)\n" +
                "Draw with the mouse (hold the left button) or with two potentiometers read by a PIC.\n" +
                "Middle click, Select Color, or the menus change the pen color.\n" +
                "Clear shakes the screen; Draw Waveforms draws sine, cosine, and tangent on a 10x10 grid.",
                "About");
        }

        private void selectColorToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            UpdatePenColor();
        }

        private void drawWaveformsToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            DrawWaveforms();
        }

        private void clearToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            ClearWithShake();
        }

        private void aboutToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Better Etch-O-Sketch (PIC Edition)\n" +
               "Draw with the mouse (hold the left button) or with two potentiometers read by a PIC.\n" +
               "Middle click, Select Color, or the menus change the pen color.\n" +
               "Clear shakes the screen; Draw Waveforms draws sine, cosine, and tangent on a 10x10 grid.",
               "About");
        }

        private void exitToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
