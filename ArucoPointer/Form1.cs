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
        // === メンバ変数 ===
        List<Mat> collectedRvecs = new List<Mat>();
        List<Mat> collectedTvecs = new List<Mat>();
        bool isCalibrating = false;
        int collectCount = 0;

        // ★計算された「先端のオフセット」
        Mat calibratedOffset = null;

        public Form1()
        {
            InitializeComponent();
            Task.Run(() => CameraLoop());
        }

        private void button1_Click(object sender, EventArgs e)
        {
            MessageBox.Show("カメラを起動します！");
            Task.Run(() => CameraLoop());
        }

        private void button2_Click(object sender, EventArgs e)
        {
            // キャリブレーション開始
            collectedRvecs.Clear();
            collectedTvecs.Clear();
            collectCount = 0;
            isCalibrating = true;
            MessageBox.Show("【先端位置の特定】\n先端を固定して、グリグリ回しながら20回撮影します。\nOKを押すと開始！");
        }

        private void CameraLoop()
        {
            // 仮のパラメータ（このままでOK）
            float markerLength = 0.05f; // ★実際のマーカーサイズ(m)に合わせてね！
            double fx = 600, cx = 320, cy = 240;

            using (var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1))
            using (var distCoeffs = new Mat(5, 1, MatType.CV_64FC1, new Scalar(0)))
            using (var capture = new VideoCapture(1)) // ★カメラ番号
            {
                // カメラ行列セット
                cameraMatrix.Set<double>(0, 0, fx); cameraMatrix.Set<double>(0, 1, 0); cameraMatrix.Set<double>(0, 2, cx);
                cameraMatrix.Set<double>(1, 0, 0); cameraMatrix.Set<double>(1, 1, fx); cameraMatrix.Set<double>(1, 2, cy);
                cameraMatrix.Set<double>(2, 0, 0); cameraMatrix.Set<double>(2, 1, 0); cameraMatrix.Set<double>(2, 2, 1.0);

                capture.Set(VideoCaptureProperties.FrameWidth, 640);
                capture.Set(VideoCaptureProperties.FrameHeight, 480);

                if (!capture.IsOpened()) { MessageBox.Show("カメラが見つかりません！"); return; }

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
                                // 枠と軸を描画
                                CvAruco.DrawDetectedMarkers(mat, corners, ids);

                                using (var rvecs = new Mat())
                                using (var tvecs = new Mat())
                                {
                                    CvAruco.EstimatePoseSingleMarkers(corners, markerLength, cameraMatrix, distCoeffs, rvecs, tvecs);

                                    // 検出されたすべてのマーカーに軸を描く
                                    for (int i = 0; i < ids.Length; i++)
                                    {
                                        Cv2.DrawFrameAxes(mat, cameraMatrix, distCoeffs, rvecs.Row(i), tvecs.Row(i), 0.1f);
                                    }

                                    // --- 1. キャリブレーション中の処理 ---
                                    if (isCalibrating)
                                    {
                                        // 0番目のマーカーを使って収集
                                        using (var rRow = rvecs.Row(0)) using (var tRow = tvecs.Row(0))
                                        {
                                            collectedRvecs.Add(rRow.Clone()); collectedTvecs.Add(tRow.Clone());
                                        }
                                        collectCount++;
                                        if (collectCount >= 20)
                                        {
                                            isCalibrating = false;
                                            calibratedOffset = CalculatePivot(collectedRvecs, collectedTvecs);
                                            MessageBox.Show("先端の位置を特定しました！\nこれで計測可能です。");
                                        }
                                        Thread.Sleep(100);
                                    }

                                    // --- 2. 計測モード（先端座標の表示） ---
                                    if (calibratedOffset != null)
                                    {
                                        // ポインター（リストの0番目にあるものと仮定）の先端位置を計算
                                        var tipPos = GetTipPosition(rvecs.Row(0), tvecs.Row(0), calibratedOffset);

                                        if (tipPos.HasValue)
                                        {
                                            var p = tipPos.Value;

                                            // 画面に数値を表示 (カメラからの距離 X, Y, Z)
                                            // X: 右方向, Y: 下方向, Z: 奥方向
                                            string text = $"Tip: X={p.X:F3}, Y={p.Y:F3}, Z={p.Z:F3}";
                                            Cv2.PutText(mat, text, new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 0.8, Scalar.Lime, 2);

                                            // 動作確認用に画面上に赤い点を打つ（計算が合ってるか確認用）
                                            var p2d = ProjectPoint(p, cameraMatrix, distCoeffs);
                                            Cv2.Circle(mat, new Point((int)p2d.X, (int)p2d.Y), 5, Scalar.Red, -1);
                                        }
                                    }
                                }
                            }

                            pictureBox1.Invoke((Action)(() => {
                                var old = pictureBox1.Image; pictureBox1.Image = BitmapConverter.ToBitmap(mat); if (old != null) old.Dispose();
                            }));
                        }
                        catch (Exception ex) { Console.WriteLine(ex.Message); }
                        Thread.Sleep(30);
                    }
                }
            }
        }

        // === 計算用メソッド（ここはずっと同じです） ===

        // 先端オフセット算出
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

        // 現在の先端位置を計算
        // 現在の先端位置を計算
        private Point3d? GetTipPosition(Mat rvecRaw, Mat tvecRaw, Mat offset)
        {
            using (var rvec = new Mat())
            using (var tvec = new Mat())
            using (var R = new Mat())
            {
                using (var rr = rvecRaw.Reshape(1)) rr.ConvertTo(rvec, MatType.CV_64FC1);
                using (var tr = tvecRaw.Reshape(1)) tr.ConvertTo(tvec, MatType.CV_64FC1);
                Cv2.Rodrigues(rvec, R);

                // ★修正ポイント： offset.T() の .T() を削除しました！
                // offsetはもともと縦長(3x1)なので、そのまま使わないと計算できません。
                // 安全のため Clone() でコピーして使います。
                using (var P_offset = offset.Clone())
                using (var R_P = (R * P_offset).ToMat())
                using (var P_cam = (R_P + tvec.T().ToMat()).ToMat())
                {
                    return new Point3d(P_cam.At<double>(0), P_cam.At<double>(1), P_cam.At<double>(2));
                }
            }
        }

        // 3D点を2D画面に投影（赤い点用）
        private Point2f ProjectPoint(Point3d p, Mat camMatrix, Mat dist)
        {
            var obj = new Mat(1, 1, MatType.CV_64FC3);
            obj.Set<Point3d>(0, 0, p);
            using (var imgPts = new Mat())
            {
                Cv2.ProjectPoints(obj, new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)), new Mat(3, 1, MatType.CV_64FC1, new Scalar(0)), camMatrix, dist, imgPts);
                return imgPts.At<Point2f>(0);
            }
        }
    }
}