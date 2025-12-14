using MasterSlave.Slave;
using MasterSlave.Client;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MasterSlave
{
    public partial class MainWindow : Window
    {
        private Master master;
        private SlaveNode currentSlave;
        private ClientApp client = new ClientApp();

        private ObservableCollection<string> masterLogs = new();
        private ObservableCollection<string> slaveLogs = new();
        private ObservableCollection<string> slaveList = new();
        private ObservableCollection<MatrixEntry> masterMatrix = new();
        private ObservableCollection<MatrixEntry> clientMatrix = new();

        public MainWindow()
        {
            InitializeComponent();
            MasterLog.ItemsSource = masterLogs;
            SlaveLog.ItemsSource = slaveLogs;
            SlaveListBox.ItemsSource = slaveList;
            MasterMatrixGrid.ItemsSource = masterMatrix;
            ClientMatrixGrid.ItemsSource = clientMatrix;

            client.OnLog += (s) => Dispatcher.Invoke(() => masterLogs.Add("[Client] " + s));
        }

        private void MasterStartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(MasterPortBox.Text, out var port)) { MessageBox.Show("Invalid port"); return; }
            master = new Master(port);
            master.OnLog += (s) => Dispatcher.Invoke(() => masterLogs.Add(s));
            master.OnSlaveListChanged += (list) => Dispatcher.Invoke(() => { slaveList.Clear(); foreach (var l in list) slaveList.Add(l); });
            master.OnMatrixReady += (m) => Dispatcher.Invoke(() => { FillMatrix(masterMatrix, m); });

            _ = master.StartAsync();
            MasterStartBtn.IsEnabled = false;
            MasterStopBtn.IsEnabled = true;
        }

        private async void MasterStopBtn_Click(object sender, RoutedEventArgs e)
        {
            if (master != null) await master.StopAsync();
            MasterStartBtn.IsEnabled = true;
            MasterStopBtn.IsEnabled = false;
        }

        private void StartSlaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var id = SlaveIdBox.Text.Trim();
            var host = SlaveHostBox.Text.Trim();
            if (!int.TryParse(SlavePortBox.Text, out var port)) { MessageBox.Show("Invalid port"); return; }
            currentSlave = new SlaveNode(id, host, port);
            currentSlave.OnLog += (s) => Dispatcher.Invoke(() => slaveLogs.Add(s));
            currentSlave.Start();
            StopSlaveBtn.IsEnabled = true;
        }

        private void StopSlaveBtn_Click(object sender, RoutedEventArgs e)
        {
            currentSlave?.Stop();
            currentSlave = null;
            StopSlaveBtn.IsEnabled = false;
        }

        private async void SubmitBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(ClientPortBox.Text, out var port)) { MessageBox.Show("Invalid port"); return; }
            var host = ClientHostBox.Text.Trim();
            var clientId = ClientIdBox.Text.Trim();
            var texts = ClientTextsBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (texts.Count == 0) { MessageBox.Show("Добавь хотя бы один текст (по одной строке)."); return; }

            client.OnLog += (s) => Dispatcher.Invoke(() => masterLogs.Add(s));
            var matrix = await client.SubmitAsync(host, port, clientId, texts);
            if (matrix != null) FillMatrix(clientMatrix, matrix);
        }

        private void FillMatrix(ObservableCollection<MatrixEntry> coll, Dictionary<string, Dictionary<string, double>> matrix)
        {
            coll.Clear();
            var keys = matrix.Keys.ToList();
            foreach (var a in keys)
                foreach (var b in keys)
                    coll.Add(new MatrixEntry { A = a, B = b, Sim = matrix[a][b] });
        }

        private void LoadFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = "Text files (*.txt)|*.txt";
            dialog.Multiselect = false;

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var lines = File.ReadAllLines(dialog.FileName, Encoding.UTF8);

                    // Если хочешь просто заменить всё содержимое — используем:
                    // ClientTextsBox.Text = string.Join("\n", lines);

                    // Но лучше добавить строки к тому, что есть:
                    if (!string.IsNullOrWhiteSpace(ClientTextsBox.Text))
                        ClientTextsBox.AppendText("\n");

                    foreach (var line in lines)
                        ClientTextsBox.AppendText(line + "\n");

                    ClientTextsBox.ScrollToEnd();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Ошибка чтения файла: " + ex.Message, "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

    }

    public class MatrixEntry
    {
        public string A { get; set; }
        public string B { get; set; }
        public double Sim { get; set; }
    }
}
