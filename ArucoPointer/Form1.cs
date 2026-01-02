using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenCvSharp.Extensions;
using OpenCvSharp.Aruco;

namespace ArucoPointer
{
    public partial class Form1 : Form
    {
        
        List<Mat> collectedRvecs = new List<Mat>(); // 回転データ
        List<Mat> collectedTvecs = new List<Mat>(); // 位置データ
        bool isCalibrating = false; // キャリブレーション中かどうか
        int collectCount = 0; // 集めた数

        Mat calibratedOffset = null; // 計算されたオフセットをここに保存

        public Form1()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            MessageBox.Show("カメラを起動します！");
            Task.Run(() => CameraLoop());
        }

        private void button2_Click(object sender, EventArgs e)
        {
            // ここで変数を触ってもエラーにならなくなります
            collectedRvecs.Clear();
            collectedTvecs.Clear();
            collectCount = 0;
            isCalibrating = true;
            MessageBox.Show("先端を固定して、指示棒を回しながらデータを集めます。\nOKを押すと開始します。");
        }

        private void CameraLoop()
        {
            // パラメータ設定
            double fx = 600;
            double cx = 320;
            double cy = 240;
            float markerLength = 0.014f;

            using (var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1))
            using (var distCoeffs = new Mat(5, 1, MatType.CV_64FC1, new Scalar(0)))
            using (var capture = new VideoCapture(1))
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

                                // ★修正ポイント2：rvecsが生きているこのブロックの中に処理を入れる！
                                if (isCalibrating)
                                {
                                    // データをコピーして保存
                                    collectedRvecs.Add(rvecs.Row(0).Clone());
                                    collectedTvecs.Add(tvecs.Row(0).Clone());
                                    collectCount++;

                                    Console.WriteLine($"データ収集中... {collectCount}/20");

                                    // 20個たまったら終了
                                    if (collectCount >= 20)
                                    {
                                        isCalibrating = false;

                                        // ★計算を実行！
                                        try
                                        {
                                            calibratedOffset = CalculatePivot(collectedRvecs, collectedTvecs);

                                            // 結果を表示してみる (メートル単位)
                                            double x = calibratedOffset.At<double>(0);
                                            double y = calibratedOffset.At<double>(1);
                                            double z = calibratedOffset.At<double>(2);

                                            MessageBox.Show($"キャリブレーション完了！\nオフセット:\nX: {x:F3}\nY: {y:F3}\nZ: {z:F3}");
                                        }
                                        catch (Exception ex)
                                        {
                                            MessageBox.Show("計算エラー: " + ex.Message);
                                        }
                                    }

                                    // データの偏りを防ぐため待機
                                    Thread.Sleep(100);
                                }
                            } // ← rvecs, tvecs はここで寿命が終わります
                        }

                        pictureBox1.Invoke((Action)(() =>
                        {
                            var oldImage = pictureBox1.Image;
                            pictureBox1.Image = BitmapConverter.ToBitmap(mat);
                            if (oldImage != null) oldImage.Dispose();
                        }));

                        Thread.Sleep(30);
                    }
                }
            }
        }

        // ★これをクラス内（CameraLoopの下など）に追加してください
        private Mat CalculatePivot(List<Mat> rvecs, List<Mat> tvecs)
        {
            int count = rvecs.Count;

            // 行列 A と B を用意
            using (var A = new Mat())
            using (var B = new Mat())
            {
                for (int i = 0; i < count; i++)
                {
                    // 1. 回転ベクトル(rvec)を回転行列(3x3)に変換
                    using (var R = new Mat())
                    using (var rvecDouble = new Mat())
                    {
                        // 型をDoubleに揃える（エラー防止）
                        rvecs[i].ConvertTo(rvecDouble, MatType.CV_64FC1);
                        Cv2.Rodrigues(rvecDouble, R);

                        // 2. -I (マイナス単位行列) を作成
                        using (var negI = -Mat.Eye(3, 3, MatType.CV_64FC1))
                        using (var rowA = new Mat())
                        {
                            // 3. [R, -I] という横長の行列(3x6)を作る
                            Cv2.HConcat(new Mat[] { R, negI }, rowA);

                            // Aに追加 (縦に積む)
                            A.PushBack(rowA);
                        }
                    }

                    // 4. Bに -T を追加 (縦に積む)
                    // ★ここが修正ポイント！
                    // tvecs[i] は「1x3（横長）」なので、「3x1（縦長）」に転置(.T())してから計算に追加します。
                    using (var tvecDouble = new Mat())
                    {
                        tvecs[i].ConvertTo(tvecDouble, MatType.CV_64FC1); // 型をDoubleに
                        using (var T_transposed = tvecDouble.T()) // 転置！
                        using (var negT = -T_transposed)
                        {
                            B.PushBack(negT);
                        }
                    }
                }

                // 5. 連立方程式を解く Ax = B
                var x = new Mat();
                // Aは (3N, 6)、Bは (3N, 1) になっているはず
                Cv2.Solve(A, B, x, DecompTypes.SVD);

                // 6. 結果の取り出し（前半3つがオフセット）
                Mat pivotOffset = x.RowRange(0, 3).Clone();

                return pivotOffset;
            }
        }
    }
}