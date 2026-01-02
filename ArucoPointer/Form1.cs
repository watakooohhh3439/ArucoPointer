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
        // ★修正ポイント1：変数をここで宣言して、ボタンからもカメラ処理からも見えるようにする（クラスのメンバ変数にする）
        List<Mat> collectedRvecs = new List<Mat>(); // 回転データ
        List<Mat> collectedTvecs = new List<Mat>(); // 位置データ
        bool isCalibrating = false; // キャリブレーション中かどうか
        int collectCount = 0; // 集めた数

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
            float markerLength = 0.05f;

            using (var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1))
            using (var distCoeffs = new Mat(5, 1, MatType.CV_64FC1, new Scalar(0)))
            using (var capture = new VideoCapture(0))
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
                                        MessageBox.Show("データ収集完了！これから計算します（未実装）");
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
    }
}