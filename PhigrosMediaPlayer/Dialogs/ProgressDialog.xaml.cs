using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Modern = iNKORE.UI.WPF.Modern.Controls;

namespace PhigrosMediaPlayer.Dialogs
{
    /// <summary>
    /// ImportingPEZDialog.xaml 的交互逻辑
    /// </summary>
    public partial class ProgressDialog : Modern.ContentDialog
    {
        public ProgressDialog(string progressName = "正在执行进程")
        {
            InitializeComponent();
            ProgressName.Text = progressName;
        }
        public void SetProgress(int completed, int total)
        {
            Dispatcher.Invoke(() =>
            {
                Progress.Value = 100 * completed / total;
                ProgressText.Text = $"{completed} / {total}";
            });
        }
    }
}
