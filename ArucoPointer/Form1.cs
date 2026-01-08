using OpenCvSharp;
using OpenCvSharp.Aruco;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing; // フォントや色のために必要
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ArucoPointer
{
    public partial class Form1 : Form
    {
        // ==========================================
        // メンバ変数
        // ==========================================
        List<Mat> collectedRvecs = new List<Mat>();
        List<Mat> collectedTvecs = new List<Mat>();

        bool isCalibrating = false;
        bool hasAutoCalibrated = false;
        int collectCount = 0;
        Mat calibratedOffset = null;

        DateTime? detectionStartTime = null;
        DateTime lastCollectTime = DateTime.MinValue;
        Mat lastCollectedRvec = null;

        Point3d? pointA = null;
        Point3d? currentTipPos = null;

        public Form1()
        {
            InitializeComponent();
            this.KeyPreview = true;
            this.KeyDown += Form1_KeyDown;

            // ★追加1: リセットボタンが Enterキーを吸い込まないようにする設定
            // (もしボタンの名前が button1 じゃない場合は、デザイナーで確認した名前に変えてください)
            button1.TabStop = false;

            // 説明ウィンドウを表示
            ShowHelpDialog();

            // ★追加2: ウィンドウを閉じた後、ボタンからフォーカスを外す
            this.ActiveControl = null;

            // カメラ開始
            Task.Run(() => CameraLoop());
        }

        // ==========================================
        // ★修正ポイント：説明ウィンドウ
        // ==========================================
        private void ShowHelpDialog()
        {
            Form helpForm = new Form();
            // ★修正: あいまいさを避けるため System.Drawing.Size と明記
            helpForm.Size = new System.Drawing.Size(500, 400);
            helpForm.Text = "使い方";
            helpForm.StartPosition = FormStartPosition.CenterScreen;
            helpForm.FormBorderStyle = FormBorderStyle.FixedDialog;
            helpForm.MaximizeBox = false;
            helpForm.MinimizeBox = false;

            // タイトル
            Label lblTitle = new Label();
            lblTitle.Text = "Aruco Pointer へようこそ";
            lblTitle.Font = new Font("MS UI Gothic", 16, FontStyle.Bold);
            lblTitle.AutoSize = true;
            // ★修正: System.Drawing.Point と明記
            lblTitle.Location = new System.Drawing.Point(20, 20);
            helpForm.Controls.Add(lblTitle);

            // 説明文
            Label lblDesc = new Label();
            lblDesc.Text =
                "このアプリは、マーカーを使って空間の距離を計測します。\n\n" +
                "【手順 1: 自動キャリブレーション】\n" +
                "・カメラにマーカーを向けてください。\n" +
                "・3秒後に自動でデータ収集が始まります。\n" +
                "・矢印の先端を固定し, いろいろな角度にグリグリ回してください。\n\n" +
                "【手順 2: 計測モード】\n" +
                "・準備完了後、自動的に計測モードになります。\n" +
                "・[Enter] キーで始点を登録できます。\n" +
                "・[R] キーでキャリブレーションをやり直せます。\n\n" 
                ;
            lblDesc.Font = new Font("MS UI Gothic", 11);
            lblDesc.AutoSize = false;
            // ★修正: System.Drawing.Size と明記
            lblDesc.Size = new System.Drawing.Size(440, 250);
            lblDesc.Location = new System.Drawing.Point(25, 60);
            helpForm.Controls.Add(lblDesc);

            // スタートボタン
            Button btnOk = new Button();
            btnOk.Text = "スタート";
            btnOk.Font = new Font("MS UI Gothic", 12, FontStyle.Bold);
            // ★修正: System.Drawing.Size と明記
            btnOk.Size = new System.Drawing.Size(120, 40);
            btnOk.Location = new System.Drawing.Point(180, 300);
            btnOk.BackColor = Color.LightBlue;
            btnOk.DialogResult = DialogResult.OK;
            helpForm.Controls.Add(btnOk);
            helpForm.AcceptButton = btnOk;

            helpForm.ShowDialog();
        }

        // === イベントハンドラ ===
        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (currentTipPos == null) return;
                if (pointA == null) pointA = currentTipPos.Value;
                else pointA = null;
            }
            if (e.KeyCode == Keys.R) ResetCalibration();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            ResetCalibration();
            pictureBox1.Focus();
        }

        private void ResetCalibration()
        {
            isCalibrating = false;
            hasAutoCalibrated = false;
            calibratedOffset = null;
            collectCount = 0;
            collectedRvecs.Clear();
            collectedTvecs.Clear();
            detectionStartTime = null;
            if (lastCollectedRvec != null) { lastCollectedRvec.Dispose(); lastCollectedRvec = null; }
            pointA = null;

            UpdateSideBar("リセット完了", "待機中...", Color.Black);
        }

        // === メイン処理ループ ===
        private void CameraLoop()
        {
            float markerLength = 0.0145f;

            // ★カメラパラメータ
            double fx = 506.25, fy = 505.10, cx = 317.89, cy = 244.05;
            double k1 = 0.2099, k2 = -0.9094, p1 = 0.0077, p2 = 0.0014, k3 = 1.3815;

            using (var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1))
            using (var distCoeffs = new Mat(5, 1, MatType.CV_64FC1))
            using (var capture = new VideoCapture(1))
            {
                cameraMatrix.Set<double>(0, 0, fx); cameraMatrix.Set<double>(0, 1, 0); cameraMatrix.Set<double>(0, 2, cx);
                cameraMatrix.Set<double>(1, 0, 0); cameraMatrix.Set<double>(1, 1, fy); cameraMatrix.Set<double>(1, 2, cy);
                cameraMatrix.Set<double>(2, 0, 0); cameraMatrix.Set<double>(2, 1, 0); cameraMatrix.Set<double>(2, 2, 1.0);
                distCoeffs.Set<double>(0, 0, k1); distCoeffs.Set<double>(1, 0, k2); distCoeffs.Set<double>(2, 0, p1);
                distCoeffs.Set<double>(3, 0, p2); distCoeffs.Set<double>(4, 0, k3);

                capture.Set(VideoCaptureProperties.FrameWidth, 640);
                capture.Set(VideoCaptureProperties.FrameHeight, 480);

                if (!capture.IsOpened()) { MessageBox.Show("カメラNG"); return; }

                using (var mat = new Mat())
                {
                    var dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
                    var parameters = new DetectorParameters();

                    while (!this.IsDisposed)
                    {
                        try
                        {
                            capture.Read(mat);
                            if (mat.Empty()) continue;

                            CvAruco.DetectMarkers(mat, dictionary, out var corners, out var ids, parameters, out var rejected);
                            currentTipPos = null;

                            if (ids.Length > 0)
                            {
                                CvAruco.DrawDetectedMarkers(mat, corners, ids);
                                using (var rvecs = new Mat()) using (var tvecs = new Mat())
                                {
                                    CvAruco.EstimatePoseSingleMarkers(corners, markerLength, cameraMatrix, distCoeffs, rvecs, tvecs);
                                    for (int i = 0; i < ids.Length; i++) Cv2.DrawFrameAxes(mat, cameraMatrix, distCoeffs, rvecs.Row(i), tvecs.Row(i), 0.1f);

                                    // --- 1. 自動開始判定 ---
                                    if (!hasAutoCalibrated && !isCalibrating)
                                    {
                                        if (detectionStartTime == null) detectionStartTime = DateTime.Now;
                                        double remaining = 3.0 - (DateTime.Now - detectionStartTime.Value).TotalSeconds;

                                        if (remaining > 0)
                                        {
                                            UpdateSideBar($"自動開始まで\n{remaining:F1}秒", "準備中", Color.Orange);
                                        }
                                        else
                                        {
                                            isCalibrating = true; hasAutoCalibrated = true;
                                            collectedRvecs.Clear(); collectedTvecs.Clear(); collectCount = 0; lastCollectedRvec = null;
                                        }
                                    }

                                    // --- 2. データ収集 ---
                                    if (isCalibrating)
                                    {
                                        using (var currentRvec = rvecs.Row(0))
                                        {
                                            bool isTimeOk = (DateTime.Now - lastCollectTime).TotalSeconds > 0.5;
                                            double diff = 0;
                                            bool isAngleOk = true;
                                            if (lastCollectedRvec != null) { diff = Cv2.Norm(currentRvec, lastCollectedRvec, NormTypes.L2); if (diff < 0.3) isAngleOk = false; }

                                            if (isTimeOk && isAngleOk)
                                            {
                                                using (var rRow = rvecs.Row(0)) using (var tRow = tvecs.Row(0)) { collectedRvecs.Add(rRow.Clone()); collectedTvecs.Add(tRow.Clone()); }
                                                if (lastCollectedRvec != null) lastCollectedRvec.Dispose();
                                                lastCollectedRvec = currentRvec.Clone(); lastCollectTime = DateTime.Now; collectCount++;

                                                UpdateSideBar($"データ収集中\n{collectCount}/20", "OK!", Color.Blue);
                                            }
                                            else
                                            {
                                                UpdateSideBar($"データ収集中\n{collectCount}/20", isTimeOk ? "もっと回して！" : "待機中...", Color.Gray);
                                            }
                                        }

                                        if (collectCount >= 20)
                                        {
                                            isCalibrating = false;
                                            if (lastCollectedRvec != null) lastCollectedRvec.Dispose();
                                            calibratedOffset = CalculatePivot(collectedRvecs, collectedTvecs);

                                            UpdateSideBar("完了！", "計測できます", Color.Green);
                                            MessageBox.Show("準備完了！計測を開始します。");
                                        }
                                    }

                                    // --- 3. 計測モード ---
                                    if (calibratedOffset != null)
                                    {
                                        var tipPos = GetTipPosition(rvecs.Row(0), tvecs.Row(0), calibratedOffset);
                                        if (tipPos.HasValue)
                                        {
                                            currentTipPos = tipPos;
                                            var pB = currentTipPos.Value;
                                            var p2d = ProjectPoint(pB, cameraMatrix, distCoeffs);

                                            Cv2.Circle(mat, new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), 10, Scalar.Red, 2);

                                            if (pointA == null)
                                            {
                                                UpdateSideBar("計測待機中", "--- cm", Color.Black);
                                                Cv2.PutText(mat, "Press [Enter] to Start", new OpenCvSharp.Point(20, 440), HersheyFonts.HersheySimplex, 0.7, Scalar.White, 2);
                                            }
                                            else
                                            {
                                                var pA = pointA.Value;
                                                double distCm = Math.Sqrt(Math.Pow(pA.X - pB.X, 2) + Math.Pow(pA.Y - pB.Y, 2) + Math.Pow(pA.Z - pB.Z, 2)) * 100.0;

                                                var pA2d = ProjectPoint(pA, cameraMatrix, distCoeffs);
                                                Cv2.Line(mat, new OpenCvSharp.Point((int)pA2d.X, (int)pA2d.Y), new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), Scalar.Cyan, 2);
                                                Cv2.Circle(mat, new OpenCvSharp.Point((int)pA2d.X, (int)pA2d.Y), 5, Scalar.Lime, -1);

                                                UpdateSideBar("計測中...", $"{distCm:F1} cm", Color.Red);
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                detectionStartTime = null;
                                UpdateSideBar("マーカー未検出", "---", Color.Gray);
                            }

                            pictureBox1.Invoke((Action)(() => {
                                var old = pictureBox1.Image;
                                pictureBox1.Image = BitmapConverter.ToBitmap(mat);
                                if (old != null) old.Dispose();
                            }));
                        }
                        catch (Exception ex) { Console.WriteLine("Loop: " + ex.Message); }
                        Thread.Sleep(30);
                    }
                }
            }
        }

        private void UpdateSideBar(string status, string distance, Color color)
        {
            try
            {
                this.Invoke((Action)(() => {
                    lblStatus.Text = status;
                    lblDistance.Text = distance;
                    lblDistance.ForeColor = color;
                }));
            }
            catch { }
        }

        private Mat CalculatePivot(List<Mat> rvecs, List<Mat> tvecs)
        {
            int count = rvecs.Count; int rows = count * 3;
            using (var A = new Mat(rows, 6, MatType.CV_64FC1)) using (var B = new Mat(rows, 1, MatType.CV_64FC1))
            {
                for (int i = 0; i < count; i++)
                {
                    using (var rvecDouble = new Mat()) using (var R = new Mat())
                    {
                        using (var rr = rvecs[i].Reshape(1)) rr.ConvertTo(rvecDouble, MatType.CV_64FC1); Cv2.Rodrigues(rvecDouble, R);
                        using (var eye = Mat.Eye(3, 3, MatType.CV_64FC1)) using (var negI = (eye * -1).ToMat()) using (var blockA = new Mat()) { Cv2.HConcat(new Mat[] { R, negI }, blockA); using (var tr = A.RowRange(i * 3, i * 3 + 3)) blockA.CopyTo(tr); }
                    }
                    using (var tvecDouble = new Mat()) { using (var tr = tvecs[i].Reshape(1)) tr.ConvertTo(tvecDouble, MatType.CV_64FC1); using (var trans = tvecDouble.T().ToMat()) using (var negT = (trans * -1).ToMat()) { using (var trB = B.RowRange(i * 3, i * 3 + 3)) negT.CopyTo(trB); } }
                }
                var x = new Mat(); Cv2.Solve(A, B, x, DecompTypes.SVD); return x.RowRange(0, 3).Clone();
            }
        }

        private Point3d? GetTipPosition(Mat rvecRaw, Mat tvecRaw, Mat offset)
        {
            using (var rvec = new Mat()) using (var tvec = new Mat()) using (var R = new Mat())
            {
                using (var rr = rvecRaw.Reshape(1)) rr.ConvertTo(rvec, MatType.CV_64FC1);
                using (var tr = tvecRaw.Reshape(1)) tr.ConvertTo(tvec, MatType.CV_64FC1);
                Cv2.Rodrigues(rvec, R);
                using (var P_offset = offset.Clone()) using (var R_P = (R * P_offset).ToMat()) using (var P_cam = (R_P + tvec.T().ToMat()).ToMat())
                    return new Point3d(P_cam.At<double>(0), P_cam.At<double>(1), P_cam.At<double>(2));
            }
        }

        private Point2f ProjectPoint(Point3d p, Mat camMatrix, Mat dist)
        {
            var obj = new Mat(1, 1, MatType.CV_64FC3); obj.Set<Point3d>(0, 0, p);
            using (var imgPts = new Mat())
            {
                Cv2.ProjectPoints(obj, new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)), new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)), camMatrix, dist, imgPts);
                Point2d p2d = imgPts.At<Point2d>(0); return new Point2f((float)p2d.X, (float)p2d.Y);
            }
        }

        private void IblStatus_Click(object sender, EventArgs e)
        {

        }

        private void IblDistance_Click(object sender, EventArgs e)
        {

        }
    }
}