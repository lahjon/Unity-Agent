using System.Windows.Controls;

namespace Spritely
{
    public partial class MainWindow
    {
        private void StatisticsTabs_IdeTabSelected()
        {
            _idePanelManager?.RefreshIfNeeded(IdeTabContent);
        }
    }
}
