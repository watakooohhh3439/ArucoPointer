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
        // クラスのメンバ変数（状態を保存しておく場所）
        // ==========================================

        // キャリブレーション用のデータをためておくリスト
        List<Mat> collectedRvecs = new List<Mat>(); // 回転データ
        List<Mat> collectedTvecs = new List<Mat>(); // 位置データ

        bool isCalibrating = false; // 今、データ収集中かどうか？
        int collectCount = 0;       // 何個データが集まったか？

        Mat calibratedOffset = null; // 計算で求めた「先端のオフセット」を保存する場所

        public Form1()
        {
            InitializeComponent();
        }

        // ==========================================
        // イベントハンドラ（ボタン操作など）
        // ==========================================

        // ボタン1：カメラを起動する
        private void button1_Click(object sender, EventArgs e)
        {
            MessageBox.Show("カメラを起動します！");
            // 重い処理なのでワーカースレッド（別作業員）に任せる
            Task.Run(() => CameraLoop());
        }

        // ボタン2：キャリブレーション（データ収集）を開始する
        private void button2_Click(object sender, EventArgs e)
        {
            // 変数をリセットして収集モードON
            collectedRvecs.Clear();
            collectedTvecs.Clear();
            collectCount = 0;
            isCalibrating = true;
            MessageBox.Show("先端を固定して、指示棒を回しながらデータを集めます。\nOKを押すと開始します。(20個集めます)");
        }

        // ==========================================
        // メイン処理（カメラ映像のループ）
        // ==========================================
        private void CameraLoop()
        {
            // パラメータ設定
            float markerLength = 0.015f; // マーカーサイズ(m)
            double fx = 600, cx = 320, cy = 240;

            using (var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1))
            using (var distCoeffs = new Mat(5, 1, MatType.CV_64FC1, new Scalar(0)))
            using (var capture = new VideoCapture(1)) // カメラ番号
            {
                cameraMatrix.Set<double>(0, 0, fx);
                cameraMatrix.Set<double>(0, 1, 0);
                cameraMatrix.Set<double>(0, 2, cx);
                cameraMatrix.Set<double>(1, 0, 0);
                cameraMatrix.Set<double>(1, 1, fx);
                cameraMatrix.Set<double>(1, 2, cy);
                cameraMatrix.Set<double>(2, 0, 0);
                cameraMatrix.Set<double>(2, 1, 0);
                cameraMatrix.Set<double>(2, 2, 1.0);

                // iVCamなどの高解像度対策
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
                                CvAruco.DrawDetectedMarkers(mat, corners, ids);

                                using (var rvecs = new Mat())
                                using (var tvecs = new Mat())
                                {
                                    CvAruco.EstimatePoseSingleMarkers(corners, markerLength, cameraMatrix, distCoeffs, rvecs, tvecs);

                                    for (int i = 0; i < ids.Length; i++)
                                    {
                                        Cv2.DrawFrameAxes(mat, cameraMatrix, distCoeffs, rvecs.Row(i), tvecs.Row(i), 0.1f);
                                    }

                                    // --- データ収集 ---
                                    if (isCalibrating)
                                    {
                                        // 安全にコピーして保存
                                        using (var rRow = rvecs.Row(0))
                                        using (var tRow = tvecs.Row(0))
                                        {
                                            collectedRvecs.Add(rRow.Clone());
                                            collectedTvecs.Add(tRow.Clone());
                                        }
                                        collectCount++;
                                        Console.WriteLine($"データ収集中... {collectCount}/20");

                                        if (collectCount >= 20)
                                        {
                                            isCalibrating = false;
                                            calibratedOffset = CalculatePivot(collectedRvecs, collectedTvecs);

                                            // 計算結果の表示
                                            double x = calibratedOffset.At<double>(0);
                                            double y = calibratedOffset.At<double>(1);
                                            double z = calibratedOffset.At<double>(2);
                                            MessageBox.Show($"完了！\nOffset: {x:F3}, {y:F3}, {z:F3}");
                                        }
                                        Thread.Sleep(100);
                                    }

                                    // ★ここに赤い丸を描く処理がありましたが、削除しました
                                }
                            }

                            pictureBox1.Invoke((Action)(() =>
                            {
                                var oldImage = pictureBox1.Image;
                                pictureBox1.Image = BitmapConverter.ToBitmap(mat);
                                if (oldImage != null) oldImage.Dispose();
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
        // 計算ロジック（ピボットキャリブレーション）
        // ==========================================
        private Mat CalculatePivot(List<Mat> rvecs, List<Mat> tvecs)
        {
            int count = rvecs.Count;
            int rows = count * 3;

            // 最初からDouble型で大きな箱を用意（エラー回避のため）
            using (var A = new Mat(rows, 6, MatType.CV_64FC1))
            using (var B = new Mat(rows, 1, MatType.CV_64FC1))
            {
                for (int i = 0; i < count; i++)
                {
                    // --- 行列 A の作成 ---
                    using (var rvecDouble = new Mat())
                    using (var R = new Mat())
                    {
                        // ★Reshape(1)を追加：データを正しく取り出す
                        using (var rvecReshaped = rvecs[i].Reshape(1))
                        {
                            rvecReshaped.ConvertTo(rvecDouble, MatType.CV_64FC1);
                        }

                        Cv2.Rodrigues(rvecDouble, R);

                        using (var eye = Mat.Eye(3, 3, MatType.CV_64FC1))
                        using (var negI = (eye * -1).ToMat())
                        using (var blockA = new Mat())
                        {
                            Cv2.HConcat(new Mat[] { R, negI }, blockA);
                            using (var targetRow = A.RowRange(i * 3, i * 3 + 3))
                            {
                                blockA.CopyTo(targetRow);
                            }
                        }
                    }

                    // --- 行列 B の作成 ---
                    using (var tvecDouble = new Mat())
                    {
                        // ★Reshape(1)を追加
                        using (var tvecReshaped = tvecs[i].Reshape(1))
                        {
                            tvecReshaped.ConvertTo(tvecDouble, MatType.CV_64FC1);
                        }

                        using (var trans = tvecDouble.T().ToMat())
                        using (var negT = (trans * -1).ToMat())
                        {
                            using (var targetRowB = B.RowRange(i * 3, i * 3 + 3))
                            {
                                negT.CopyTo(targetRowB);
                            }
                        }
                    }
                }

                // 連立方程式を解く (SVD法)
                var x = new Mat();
                Cv2.Solve(A, B, x, DecompTypes.SVD);

                // 結果の前半3つがオフセット座標
                return x.RowRange(0, 3).Clone();
            }
        }
    }
}