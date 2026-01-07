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
        // メンバ変数（状態管理）
        // ==========================================
        List<Mat> collectedRvecs = new List<Mat>();
        List<Mat> collectedTvecs = new List<Mat>();

        bool isCalibrating = false; // データ収集中か？
        int collectCount = 0;       // 集めた数

        Mat calibratedOffset = null; // ★計算された先端位置（ゴール）

        // 自動開始用の変数
        bool hasAutoCalibrated = false;
        DateTime? detectionStartTime = null;

        // 厳選収録用の変数
        DateTime lastCollectTime = DateTime.MinValue;
        Mat lastCollectedRvec = null;

        public Form1()
        {
            InitializeComponent();
            // アプリ起動と同時にカメラ処理を開始
            Task.Run(() => CameraLoop());
        }

        // ==========================================
        // メイン処理ループ
        // ==========================================
        private void CameraLoop()
        {
            // ★設定：実際のマーカーサイズ(m)に合わせて変更してください
            float markerLength = 0.0145f;

            // カメラ内部パラメータ（仮定値）
            double fx = 600, cx = 320, cy = 240;

            using (var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1))
            using (var distCoeffs = new Mat(5, 1, MatType.CV_64FC1, new Scalar(0)))
            using (var capture = new VideoCapture(1)) // ★カメラ番号（映らない場合は 0 に変更）
            {
                // カメラ行列の初期化
                cameraMatrix.Set<double>(0, 0, fx); cameraMatrix.Set<double>(0, 1, 0); cameraMatrix.Set<double>(0, 2, cx);
                cameraMatrix.Set<double>(1, 0, 0); cameraMatrix.Set<double>(1, 1, fx); cameraMatrix.Set<double>(1, 2, cy);
                cameraMatrix.Set<double>(2, 0, 0); cameraMatrix.Set<double>(2, 1, 0); cameraMatrix.Set<double>(2, 2, 1.0);

                // 解像度指定 (iVCam等の高解像度対策)
                capture.Set(VideoCaptureProperties.FrameWidth, 640);
                capture.Set(VideoCaptureProperties.FrameHeight, 480);

                if (!capture.IsOpened())
                {
                    MessageBox.Show("カメラが見つかりません！");
                    return;
                }

                using (var mat = new Mat())
                {
                    var dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
                    var parameters = new DetectorParameters();

                    while (true)
                    {
                        try
                        {
                            capture.Read(mat);
                            if (mat.Empty()) continue;

                            CvAruco.DetectMarkers(mat, dictionary, out var corners, out var ids, parameters, out var rejected);

                            if (ids.Length > 0)
                            {
                                // 枠線と軸の描画
                                CvAruco.DrawDetectedMarkers(mat, corners, ids);
                                using (var rvecs = new Mat())
                                using (var tvecs = new Mat())
                                {
                                    CvAruco.EstimatePoseSingleMarkers(corners, markerLength, cameraMatrix, distCoeffs, rvecs, tvecs);

                                    for (int i = 0; i < ids.Length; i++)
                                    {
                                        Cv2.DrawFrameAxes(mat, cameraMatrix, distCoeffs, rvecs.Row(i), tvecs.Row(i), 0.1f);
                                    }

                                    // --------------------------------------------------
                                    // 1. 自動キャリブレーション開始判定 (3秒カウントダウン)
                                    // --------------------------------------------------
                                    if (!hasAutoCalibrated && !isCalibrating)
                                    {
                                        if (detectionStartTime == null) detectionStartTime = DateTime.Now;

                                        double remaining = 3.0 - (DateTime.Now - detectionStartTime.Value).TotalSeconds;

                                        if (remaining > 0)
                                        {
                                            Cv2.PutText(mat, $"Auto Start in: {remaining:F1}s", new OpenCvSharp.Point(20, 400), HersheyFonts.HersheySimplex, 1.0, Scalar.Yellow, 2);
                                        }
                                        else
                                        {
                                            // 開始！
                                            isCalibrating = true;
                                            hasAutoCalibrated = true;
                                            collectedRvecs.Clear();
                                            collectedTvecs.Clear();
                                            collectCount = 0;
                                            lastCollectedRvec = null;
                                        }
                                    }

                                    // --------------------------------------------------
                                    // 2. データ収集 (厳選フィルタリング付き)
                                    // --------------------------------------------------
                                    if (isCalibrating)
                                    {
                                        using (var currentRvec = rvecs.Row(0))
                                        {
                                            // 時間チェック(0.5秒) & 角度チェック(0.3以上変化)
                                            bool isTimeOk = (DateTime.Now - lastCollectTime).TotalSeconds > 0.5;
                                            double diff = 0;
                                            bool isAngleOk = true;

                                            if (lastCollectedRvec != null)
                                            {
                                                diff = Cv2.Norm(currentRvec, lastCollectedRvec, NormTypes.L2);
                                                if (diff < 0.3) isAngleOk = false;
                                            }

                                            if (isTimeOk && isAngleOk)
                                            {
                                                // 採用！
                                                using (var rRow = rvecs.Row(0)) using (var tRow = tvecs.Row(0))
                                                {
                                                    collectedRvecs.Add(rRow.Clone());
                                                    collectedTvecs.Add(tRow.Clone());
                                                }

                                                if (lastCollectedRvec != null) lastCollectedRvec.Dispose();
                                                lastCollectedRvec = currentRvec.Clone();
                                                lastCollectTime = DateTime.Now;
                                                collectCount++;

                                                Cv2.PutText(mat, "Captured!", new OpenCvSharp.Point(20, 350), HersheyFonts.HersheySimplex, 1.0, Scalar.Yellow, 2);
                                            }
                                            else
                                            {
                                                string msg = isTimeOk ? $"Rotate More! (Diff: {diff:F2})" : "Wait...";
                                                Cv2.PutText(mat, msg, new OpenCvSharp.Point(20, 350), HersheyFonts.HersheySimplex, 0.7, Scalar.Gray, 2);
                                            }
                                        }

                                        Cv2.PutText(mat, $"Collecting... {collectCount}/20", new OpenCvSharp.Point(20, 400), HersheyFonts.HersheySimplex, 1.0, Scalar.Cyan, 2);

                                        if (collectCount >= 20)
                                        {
                                            isCalibrating = false;
                                            if (lastCollectedRvec != null) { lastCollectedRvec.Dispose(); lastCollectedRvec = null; }

                                            // 計算実行
                                            calibratedOffset = CalculatePivot(collectedRvecs, collectedTvecs);
                                            MessageBox.Show("キャリブレーション完了！\n先端トラッキングを開始します。");
                                        }
                                        Thread.Sleep(10); // 短くてOK
                                    }

                                    // --------------------------------------------------
                                    // 3. 計測モード (先端トラッキング)
                                    // --------------------------------------------------
                                    if (calibratedOffset != null)
                                    {
                                        var tipPos = GetTipPosition(rvecs.Row(0), tvecs.Row(0), calibratedOffset);
                                        if (tipPos.HasValue)
                                        {
                                            var p = tipPos.Value;

                                            // 座標表示
                                            string text = $"Tip: X={p.X:F3}, Y={p.Y:F3}, Z={p.Z:F3}";
                                            Cv2.PutText(mat, text, new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 0.8, Scalar.Lime, 2);

                                            // 照準マーク表示
                                            var p2d = ProjectPoint(p, cameraMatrix, distCoeffs);
                                            Cv2.Circle(mat, new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), 6, Scalar.Red, -1);
                                            Cv2.Circle(mat, new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), 10, Scalar.Yellow, 2);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // マーカーロスト時はカウントダウンリセット
                                detectionStartTime = null;
                            }

                            // 画面更新
                            pictureBox1.Invoke((Action)(() => {
                                var old = pictureBox1.Image;
                                pictureBox1.Image = BitmapConverter.ToBitmap(mat);
                                if (old != null) old.Dispose();
                            }));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Loop Error: " + ex.Message);
                        }

                        Thread.Sleep(30);
                    }
                }
            }
        }

        // ==========================================
        // 計算メソッド集
        // ==========================================

        // ピボットキャリブレーション計算
        private Mat CalculatePivot(List<Mat> rvecs, List<Mat> tvecs)
        {
            int count = rvecs.Count;
            int rows = count * 3;
            using (var A = new Mat(rows, 6, MatType.CV_64FC1))
            using (var B = new Mat(rows, 1, MatType.CV_64FC1))
            {
                for (int i = 0; i < count; i++)
                {
                    using (var rvecDouble = new Mat()) using (var R = new Mat())
                    {
                        using (var rr = rvecs[i].Reshape(1)) rr.ConvertTo(rvecDouble, MatType.CV_64FC1);
                        Cv2.Rodrigues(rvecDouble, R);
                        using (var eye = Mat.Eye(3, 3, MatType.CV_64FC1)) using (var negI = (eye * -1).ToMat()) using (var blockA = new Mat())
                        {
                            Cv2.HConcat(new Mat[] { R, negI }, blockA);
                            using (var targetRow = A.RowRange(i * 3, i * 3 + 3)) blockA.CopyTo(targetRow);
                        }
                    }
                    using (var tvecDouble = new Mat())
                    {
                        using (var tr = tvecs[i].Reshape(1)) tr.ConvertTo(tvecDouble, MatType.CV_64FC1);
                        using (var trans = tvecDouble.T().ToMat()) using (var negT = (trans * -1).ToMat())
                        {
                            using (var trB = B.RowRange(i * 3, i * 3 + 3)) negT.CopyTo(trB);
                        }
                    }
                }
                var x = new Mat();
                Cv2.Solve(A, B, x, DecompTypes.SVD);
                return x.RowRange(0, 3).Clone();
            }
        }

        // 先端位置の計算
        private Point3d? GetTipPosition(Mat rvecRaw, Mat tvecRaw, Mat offset)
        {
            using (var rvec = new Mat()) using (var tvec = new Mat()) using (var R = new Mat())
            {
                using (var rr = rvecRaw.Reshape(1)) rr.ConvertTo(rvec, MatType.CV_64FC1);
                using (var tr = tvecRaw.Reshape(1)) tr.ConvertTo(tvec, MatType.CV_64FC1);
                Cv2.Rodrigues(rvec, R);

                // Offsetをコピーして使う（.T()は不要。offsetは元々3x1）
                using (var P_offset = offset.Clone())
                using (var R_P = (R * P_offset).ToMat())
                using (var P_cam = (R_P + tvec.T().ToMat()).ToMat())
                {
                    return new Point3d(P_cam.At<double>(0), P_cam.At<double>(1), P_cam.At<double>(2));
                }
            }
        }

        // 3D点 -> 2D画面投影（照準用）
        private Point2f ProjectPoint(Point3d p, Mat camMatrix, Mat dist)
        {
            var obj = new Mat(1, 1, MatType.CV_64FC3);
            obj.Set<Point3d>(0, 0, p);
            using (var imgPts = new Mat())
            {
                // 正しいスカラー型の0を指定
                Cv2.ProjectPoints(
                    obj,
                    new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)),
                    new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)),
                    camMatrix,
                    dist,
                    imgPts);

                // Double型(Point2d)で取り出してからFloat(Point2f)に変換
                Point2d p2d = imgPts.At<Point2d>(0);
                return new Point2f((float)p2d.X, (float)p2d.Y);
            }
        }
    }
}