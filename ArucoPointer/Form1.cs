using OpenCvSharp;
using OpenCvSharp.Aruco;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
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
        int collectCount = 0;
        Mat calibratedOffset = null; // ★計算された先端位置

        // 自動開始・厳選収集用
        bool hasAutoCalibrated = false;
        DateTime? detectionStartTime = null;
        DateTime lastCollectTime = DateTime.MinValue;
        Mat lastCollectedRvec = null;

        // 計測用
        Point3d? pointA = null; // 始点

        public Form1()
        {
            InitializeComponent();
            this.KeyPreview = true;
            this.KeyDown += Form1_KeyDown; // Spaceキー判定用
            Task.Run(() => CameraLoop());
        }

        
        // Spaceキー操作
        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (currentTipPos == null) return;

                if (pointA == null) pointA = currentTipPos.Value; // 始点登録
                else pointA = null; // リセット
            }
        }

        Point3d? currentTipPos = null;

        private void CameraLoop()
        {
            float markerLength = 0.05f;

            // ===============================================
            // ★以前教えてもらった数値をセット済みです！
            // ===============================================
            double fx = 506.25;
            double fy = 505.10;
            double cx = 317.89;
            double cy = 244.05;

            double k1 = 0.2099;
            double k2 = -0.9094;
            double p1 = 0.0077;
            double p2 = 0.0014;
            double k3 = 1.3815;
            // ===============================================

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
                                            Cv2.PutText(mat, $"Auto Calib in: {remaining:F1}s", new OpenCvSharp.Point(20, 400), HersheyFonts.HersheySimplex, 1.0, Scalar.Yellow, 2);
                                        else
                                        {
                                            // スタート！
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
                                                Cv2.PutText(mat, "Captured!", new OpenCvSharp.Point(20, 350), HersheyFonts.HersheySimplex, 1.0, Scalar.Yellow, 2);
                                            }
                                            else { string msg = isTimeOk ? $"Rotate More! ({diff:F2})" : "Wait..."; Cv2.PutText(mat, msg, new OpenCvSharp.Point(20, 350), HersheyFonts.HersheySimplex, 0.7, Scalar.Gray, 2); }
                                        }
                                        Cv2.PutText(mat, $"Collecting... {collectCount}/20", new OpenCvSharp.Point(20, 400), HersheyFonts.HersheySimplex, 1.0, Scalar.Cyan, 2);

                                        if (collectCount >= 20)
                                        {
                                            isCalibrating = false;
                                            if (lastCollectedRvec != null) lastCollectedRvec.Dispose();
                                            calibratedOffset = CalculatePivot(collectedRvecs, collectedTvecs);
                                            MessageBox.Show("完了！計測を開始します。");
                                        }
                                    }

                                    // --- 3. 計測モード ---
                                    if (calibratedOffset != null)
                                    {
                                        currentTipPos = GetTipPosition(rvecs.Row(0), tvecs.Row(0), calibratedOffset);
                                        if (currentTipPos.HasValue)
                                        {
                                            var pB = currentTipPos.Value;
                                            var p2d = ProjectPoint(pB, cameraMatrix, distCoeffs);

                                            // 照準
                                            Cv2.Circle(mat, new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), 6, Scalar.Red, -1);
                                            Cv2.Circle(mat, new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), 10, Scalar.Yellow, 2);

                                            if (pointA == null)
                                            {
                                                Cv2.PutText(mat, "Enter: Set Start Point", new OpenCvSharp.Point(20, 40), HersheyFonts.HersheySimplex, 0.8, Scalar.White, 2);
                                            }
                                            else
                                            {
                                                var pA = pointA.Value;
                                                double distCm = Math.Sqrt(Math.Pow(pA.X - pB.X, 2) + Math.Pow(pA.Y - pB.Y, 2) + Math.Pow(pA.Z - pB.Z, 2)) * 100.0;

                                                var pA2d = ProjectPoint(pA, cameraMatrix, distCoeffs);
                                                Cv2.Line(mat, new OpenCvSharp.Point((int)pA2d.X, (int)pA2d.Y), new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), Scalar.Cyan, 2);
                                                Cv2.Circle(mat, new OpenCvSharp.Point((int)pA2d.X, (int)pA2d.Y), 8, Scalar.Lime, -1);
                                                Cv2.PutText(mat, $"{distCm:F1} cm", new OpenCvSharp.Point(20, 80), HersheyFonts.HersheySimplex, 1.5, Scalar.Cyan, 3);
                                            }
                                        }
                                    }
                                }
                            }
                            else { detectionStartTime = null; }

                            pictureBox1.Invoke((Action)(() => { var old = pictureBox1.Image; pictureBox1.Image = BitmapConverter.ToBitmap(mat); if (old != null) old.Dispose(); }));
                        }
                        catch (Exception ex) { Console.WriteLine("Loop: " + ex.Message); }
                        Thread.Sleep(30);
                    }
                }
            }
        }

        // === 計算ロジック ===
        private Mat CalculatePivot(List<Mat> rvecs, List<Mat> tvecs)
        {
            int c = rvecs.Count, r = c * 3;
            using (var A = new Mat(r, 6, MatType.CV_64FC1)) using (var B = new Mat(r, 1, MatType.CV_64FC1))
            {
                for (int i = 0; i < c; i++)
                {
                    using (var rd = new Mat()) using (var R = new Mat())
                    {
                        using (var rs = rvecs[i].Reshape(1)) rs.ConvertTo(rd, MatType.CV_64FC1); Cv2.Rodrigues(rd, R);
                        using (var eye = Mat.Eye(3, 3, MatType.CV_64FC1)) using (var negI = (eye * -1).ToMat()) using (var blkA = new Mat()) { Cv2.HConcat(new Mat[] { R, negI }, blkA); using (var tr = A.RowRange(i * 3, i * 3 + 3)) blkA.CopyTo(tr); }
                    }
                    using (var td = new Mat()) { using (var ts = tvecs[i].Reshape(1)) ts.ConvertTo(td, MatType.CV_64FC1); using (var tr = td.T().ToMat()) using (var nt = (tr * -1).ToMat()) { using (var trB = B.RowRange(i * 3, i * 3 + 3)) nt.CopyTo(trB); } }
                }
                var x = new Mat(); Cv2.Solve(A, B, x, DecompTypes.SVD); return x.RowRange(0, 3).Clone();
            }
        }

        private Point3d? GetTipPosition(Mat rvecRaw, Mat tvecRaw, Mat offset)
        {
            using (var r = new Mat()) using (var t = new Mat()) using (var R = new Mat())
            {
                using (var rs = rvecRaw.Reshape(1)) rs.ConvertTo(r, MatType.CV_64FC1);
                using (var ts = tvecRaw.Reshape(1)) ts.ConvertTo(t, MatType.CV_64FC1);
                Cv2.Rodrigues(r, R);
                using (var off = offset.Clone()) using (var rp = (R * off).ToMat()) using (var p = (rp + t.T().ToMat()).ToMat())
                    return new Point3d(p.At<double>(0), p.At<double>(1), p.At<double>(2));
            }
        }

        private Point2f ProjectPoint(Point3d p, Mat cam, Mat dist)
        {
            var obj = new Mat(1, 1, MatType.CV_64FC3); obj.Set<Point3d>(0, 0, p);
            using (var img = new Mat())
            {
                Cv2.ProjectPoints(obj, new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)), new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)), cam, dist, img);
                var p2d = img.At<Point2d>(0); return new Point2f((float)p2d.X, (float)p2d.Y);
            }
        }

        private void button1_Click_1(object sender, EventArgs e)
        {
            ResetCalibration();

            // ★重要：ボタンからフォーカスを外す（これをしないとSpace/Enterがボタンに吸われる）
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

            MessageBox.Show("リセットしました。\nマーカーを映すと再キャリブレーションを開始します。");
        }
    }
}