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

        // データ収集用リスト
        List<Mat> collectedRvecs = new List<Mat>();
        List<Mat> collectedTvecs = new List<Mat>();

        // 状態フラグ
        bool isCalibrating = false;      // 現在データを収集中か
        bool hasAutoCalibrated = false;  // 自動開始が済んだか
        int collectCount = 0;            // 集めたデータの数

        // ★計算結果（先端のズレ）
        Mat calibratedOffset = null;

        // 自動開始ロジック用
        DateTime? detectionStartTime = null;

        // 厳選収録（フィルタリング）用
        DateTime lastCollectTime = DateTime.MinValue;
        Mat lastCollectedRvec = null;

        // 計測用（始点）
        Point3d? pointA = null;

        // 現在の先端位置（キーイベント共有用）
        Point3d? currentTipPos = null;

        public Form1()
        {
            InitializeComponent();

            // キー操作を受け付ける設定
            this.KeyPreview = true;
            this.KeyDown += Form1_KeyDown;

            // アプリ起動と同時にカメラ処理を開始
            Task.Run(() => CameraLoop());
        }

        // ==========================================
        // イベントハンドラ (キー操作 & ボタン)
        // ==========================================

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            // Enterキー: 計測の始点登録 / リセット
            if (e.KeyCode == Keys.Enter)
            {
                if (currentTipPos == null) return;

                if (pointA == null)
                {
                    pointA = currentTipPos.Value; // A地点（始点）を登録
                }
                else
                {
                    pointA = null; // 計測リセット
                }
            }

            // Rキー: 強制リセット（再キャリブレーション）
            if (e.KeyCode == Keys.R)
            {
                ResetCalibration();
            }
        }

        // 画面上のボタンが押されたとき
        private void button1_Click(object sender, EventArgs e)
        {
            ResetCalibration();
            // フォーカスを画像に移す（これをしないとEnterキーがボタンに吸われる）
            pictureBox1.Focus();
        }

        // リセット処理（共通）
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

            pointA = null; // 計測中のデータも消去

            MessageBox.Show("リセットしました。\nマーカーを映すと、3秒後にデータ収集を開始します。");
        }

        // ==========================================
        // メイン処理ループ
        // ==========================================
        private void CameraLoop()
        {
            // ★マーカーのサイズ (メートル)
            // 5cmなら 0.05f。もし 1.45cm なら 0.0145f に変更してください。
            float markerLength = 0.0145f;

            // ===============================================
            // ★カメラパラメータ（測定済みデータ）
            // ===============================================
            double fx = 516.67;
            double fy = 516.58;
            double cx = 322.46;
            double cy = 222.63;

            double k1 = 0.1708;
            double k2 = -0.1853;
            double p1 = -0.0126;
            double p2 = 0.0029;
            double k3 = -0.3686;
            // ===============================================

            using (var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1))
            using (var distCoeffs = new Mat(5, 1, MatType.CV_64FC1))
            using (var capture = new VideoCapture(1)) // ★映らない場合は 0 に変更
            {
                // カメラ行列セット
                cameraMatrix.Set<double>(0, 0, fx); cameraMatrix.Set<double>(0, 1, 0); cameraMatrix.Set<double>(0, 2, cx);
                cameraMatrix.Set<double>(1, 0, 0); cameraMatrix.Set<double>(1, 1, fy); cameraMatrix.Set<double>(1, 2, cy);
                cameraMatrix.Set<double>(2, 0, 0); cameraMatrix.Set<double>(2, 1, 0); cameraMatrix.Set<double>(2, 2, 1.0);

                // 歪み係数セット
                distCoeffs.Set<double>(0, 0, k1); distCoeffs.Set<double>(1, 0, k2); distCoeffs.Set<double>(2, 0, p1);
                distCoeffs.Set<double>(3, 0, p2); distCoeffs.Set<double>(4, 0, k3);

                // 解像度設定
                capture.Set(VideoCaptureProperties.FrameWidth, 640);
                capture.Set(VideoCaptureProperties.FrameHeight, 480);

                if (!capture.IsOpened()) { MessageBox.Show("カメラが見つかりません！"); return; }

                using (var mat = new Mat())
                {
                    var dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
                    var parameters = new DetectorParameters();

                    // ループ開始
                    while (!this.IsDisposed)
                    {
                        try
                        {
                            capture.Read(mat);
                            if (mat.Empty()) continue;

                            CvAruco.DetectMarkers(mat, dictionary, out var corners, out var ids, parameters, out var rejected);

                            currentTipPos = null; // 毎フレームリセット

                            if (ids.Length > 0)
                            {
                                CvAruco.DrawDetectedMarkers(mat, corners, ids);
                                using (var rvecs = new Mat()) using (var tvecs = new Mat())
                                {
                                    CvAruco.EstimatePoseSingleMarkers(corners, markerLength, cameraMatrix, distCoeffs, rvecs, tvecs);

                                    for (int i = 0; i < ids.Length; i++)
                                    {
                                        Cv2.DrawFrameAxes(mat, cameraMatrix, distCoeffs, rvecs.Row(i), tvecs.Row(i), 0.1f);
                                    }

                                    // --------------------------------------------------
                                    // 1. 自動開始カウントダウン
                                    // --------------------------------------------------
                                    if (!hasAutoCalibrated && !isCalibrating)
                                    {
                                        if (detectionStartTime == null) detectionStartTime = DateTime.Now;

                                        double remaining = 3.0 - (DateTime.Now - detectionStartTime.Value).TotalSeconds;

                                        if (remaining > 0)
                                        {
                                            Cv2.PutText(mat, $"Auto Start: {remaining:F1}s", new OpenCvSharp.Point(20, 400), HersheyFonts.HersheySimplex, 1.0, Scalar.Yellow, 2);
                                        }
                                        else
                                        {
                                            // 3秒経過！収集スタート
                                            isCalibrating = true;
                                            hasAutoCalibrated = true;
                                            collectedRvecs.Clear();
                                            collectedTvecs.Clear();
                                            collectCount = 0;
                                            lastCollectedRvec = null;
                                        }
                                    }

                                    // --------------------------------------------------
                                    // 2. データ収集 (キャリブレーション中)
                                    // --------------------------------------------------
                                    if (isCalibrating)
                                    {
                                        using (var currentRvec = rvecs.Row(0))
                                        {
                                            // 厳選ロジック (時間 & 角度変化)
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
                                                // 採用
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
                                            MessageBox.Show("キャリブレーション完了！\n先端計測モードに入ります。\n\n[Enter]キーで始点を登録できます。");
                                        }
                                    }

                                    // --------------------------------------------------
                                    // 3. 計測モード (キャリブレーション完了後)
                                    // --------------------------------------------------
                                    if (calibratedOffset != null)
                                    {
                                        var tipPos = GetTipPosition(rvecs.Row(0), tvecs.Row(0), calibratedOffset);
                                        if (tipPos.HasValue)
                                        {
                                            currentTipPos = tipPos;
                                            var pB = currentTipPos.Value; // 現在の先端位置

                                            // 照準マーク
                                            var p2d = ProjectPoint(pB, cameraMatrix, distCoeffs);
                                            Cv2.Circle(mat, new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), 6, Scalar.Red, -1);
                                            Cv2.Circle(mat, new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), 10, Scalar.Yellow, 2);

                                            // 座標表示
                                            string text = $"Tip: {pB.X:F2}, {pB.Y:F2}, {pB.Z:F2}";
                                            Cv2.PutText(mat, text, new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 0.6, Scalar.Lime, 1);

                                            // 計測ロジック
                                            if (pointA == null)
                                            {
                                                Cv2.PutText(mat, "Press [Enter] to Set Start Point", new OpenCvSharp.Point(20, 440), HersheyFonts.HersheySimplex, 0.7, Scalar.White, 2);
                                            }
                                            else
                                            {
                                                var pA = pointA.Value;
                                                // 距離計算 (cm)
                                                double distCm = Math.Sqrt(Math.Pow(pA.X - pB.X, 2) + Math.Pow(pA.Y - pB.Y, 2) + Math.Pow(pA.Z - pB.Z, 2)) * 100.0;

                                                // 線を描画
                                                var pA2d = ProjectPoint(pA, cameraMatrix, distCoeffs);
                                                Cv2.Line(mat, new OpenCvSharp.Point((int)pA2d.X, (int)pA2d.Y), new OpenCvSharp.Point((int)p2d.X, (int)p2d.Y), Scalar.Cyan, 2);
                                                Cv2.Circle(mat, new OpenCvSharp.Point((int)pA2d.X, (int)pA2d.Y), 8, Scalar.Lime, -1);

                                                Cv2.PutText(mat, $"{distCm:F1} cm", new OpenCvSharp.Point(20, 80), HersheyFonts.HersheySimplex, 1.5, Scalar.Cyan, 3);
                                                Cv2.PutText(mat, "Press [Enter] to Reset", new OpenCvSharp.Point(20, 440), HersheyFonts.HersheySimplex, 0.7, Scalar.Gray, 1);
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                detectionStartTime = null; // マーカーロスト時はタイマーリセット
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
                Cv2.ProjectPoints(
                    obj,
                    new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)),
                    new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)),
                    camMatrix,
                    dist,
                    imgPts);

                Point2d p2d = imgPts.At<Point2d>(0);
                return new Point2f((float)p2d.X, (float)p2d.Y);
            }
        }
    }
}