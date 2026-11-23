using Microsoft.Data.SqlClient;
using System.Windows;
using System.Windows.Controls;

namespace QuantumCore.App;

public partial class SqlConnectionManagerWindow : Window
{
    private List<SavedSqlConnection> _connections = [];
    private SavedSqlConnection? _activeConnection;
    private bool _loadingSelection;

    public SqlConnectionManagerWindow()
    {
        InitializeComponent();
        _connections = SqlConnectionStore.Load();
        SavedConnectionsCombo.ItemsSource = _connections;
        DiscoverLocalServers();
        if (_connections.Count > 0) SavedConnectionsCombo.SelectedIndex = 0;
    }

    private void Discover_Click(object sender, RoutedEventArgs e) => DiscoverLocalServers();

    private void DiscoverLocalServers()
    {
        var servers = SqlServerDiscovery.FindLocalInstances();
        DiscoveredServersCombo.ItemsSource = servers;
        if (servers.Count > 0)
        {
            DiscoveredServersCombo.SelectedIndex = 0;
            StatusText.Text = $"Found {servers.Count} possible local SQL instance(s). You can also type a server name manually.";
        }
        else
            StatusText.Text = "No local SQL instances were detected. Enter a server or instance name manually.";
    }

    private void DiscoveredServersCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiscoveredServersCombo.SelectedItem is string server) ServerBox.Text = server;
    }

    private void AuthenticationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool sqlAuthentication = AuthenticationCombo.SelectedIndex == 1;
        if (UsernameBox is null || PasswordBox is null) return;
        UsernameBox.IsEnabled = sqlAuthentication;
        PasswordBox.IsEnabled = sqlAuthentication;
    }

    private void SavedConnectionsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection || SavedConnectionsCombo.SelectedItem is not SavedSqlConnection connection) return;
        _activeConnection = connection;
        NameBox.Text = connection.Name;
        ServerBox.Text = connection.Server;
        BackupDirectoryBox.Text = connection.BackupDirectory;
        AuthenticationCombo.SelectedIndex = connection.UseWindowsAuthentication ? 0 : 1;
        UsernameBox.Text = connection.Username;
        PasswordBox.Password = "";
        DatabasesList.ItemsSource = null;
        DatabaseStatusText.Text = "Test the connection to load databases.";
        StatusText.Text = "Saved connection selected. Re-enter its password to edit SQL authentication details.";
    }

    private void NewConnection_Click(object sender, RoutedEventArgs e)
    {
        _loadingSelection = true;
        SavedConnectionsCombo.SelectedItem = null;
        _loadingSelection = false;
        _activeConnection = null;
        NameBox.Text = "";
        ServerBox.Text = "";
        BackupDirectoryBox.Text = "";
        AuthenticationCombo.SelectedIndex = 0;
        UsernameBox.Text = "";
        PasswordBox.Password = "";
        DatabasesList.ItemsSource = null;
        DatabaseStatusText.Text = "Connect to a SQL Server to list its databases.";
        StatusText.Text = "Enter the details for a new SQL connection.";
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildConnection(out var connection, out var error))
        {
            MessageBox.Show(this, error, "Connection details required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText.Text = "Connecting to SQL Server…";
        DatabaseStatusText.Text = "Loading databases…";
        SetBusy(true);
        try
        {
            await using var sql = new SqlConnection(connection.GetConnectionString());
            await sql.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = N'ONLINE' ORDER BY name;", sql);
            await using var reader = await command.ExecuteReaderAsync();
            var databases = new List<string>();
            while (await reader.ReadAsync()) databases.Add(reader.GetString(0));
            DatabasesList.ItemsSource = databases;
            DatabaseStatusText.Text = databases.Count == 0
                ? "Connected, but no online user databases were found."
                : $"{databases.Count} online user database(s). Select one for backup.";
            StatusText.Text = $"Connected successfully to {connection.Server}.";
            _activeConnection = connection;
        }
        catch (Exception ex)
        {
            DatabasesList.ItemsSource = null;
            DatabaseStatusText.Text = "Could not load databases.";
            StatusText.Text = "Connection failed.";
            MessageBox.Show(this, ex.Message, "SQL connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildConnection(out var connection, out var error))
        {
            MessageBox.Show(this, error, "Connection details required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existing = _activeConnection is null
            ? null
            : _connections.FirstOrDefault(c => c.Id == _activeConnection.Id);
        if (existing is null)
        {
            connection.Id = Guid.NewGuid().ToString("N");
            _connections.Add(connection);
        }
        else
        {
            connection.Id = existing.Id;
            _connections[_connections.IndexOf(existing)] = connection;
        }

        try
        {
            SqlConnectionStore.Save(_connections);
            _activeConnection = connection;
            _loadingSelection = true;
            SavedConnectionsCombo.ItemsSource = null;
            SavedConnectionsCombo.ItemsSource = _connections;
            SavedConnectionsCombo.SelectedItem = connection;
            _loadingSelection = false;
            PasswordBox.Password = "";
            StatusText.Text = "Connection saved securely on this Windows user profile.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save connection", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_activeConnection is null) return;
        var answer = MessageBox.Show(this, $"Remove the saved connection '{_activeConnection.Name}'?", "Remove connection",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _connections.RemoveAll(c => c.Id == _activeConnection.Id);
        try
        {
            SqlConnectionStore.Save(_connections);
            NewConnection_Click(sender, e);
            _loadingSelection = true;
            SavedConnectionsCombo.ItemsSource = null;
            SavedConnectionsCombo.ItemsSource = _connections;
            _loadingSelection = false;
            StatusText.Text = "Saved connection removed.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not remove connection", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryBuildConnection(out SavedSqlConnection connection, out string error)
    {
        connection = new SavedSqlConnection();
        error = "";
        string server = ServerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(server)) { error = "Enter or discover a SQL Server / instance."; return false; }
        string name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = server;
        bool windowsAuth = AuthenticationCombo.SelectedIndex != 1;
        string username = UsernameBox.Text.Trim();
        if (!windowsAuth && string.IsNullOrWhiteSpace(username)) { error = "Enter the SQL Server username."; return false; }

        string password = PasswordBox.Password;
        string? protectedPassword = null;
        if (!windowsAuth)
        {
            if (string.IsNullOrEmpty(password) && _activeConnection is not null
                && !_activeConnection.UseWindowsAuthentication && _activeConnection.Server.Equals(server, StringComparison.OrdinalIgnoreCase))
                protectedPassword = _activeConnection.ProtectedPassword;
            else
                protectedPassword = SqlConnectionStore.ProtectPassword(password);
            if (string.IsNullOrEmpty(protectedPassword)) { error = "Enter the SQL Server password."; return false; }
        }

        connection = new SavedSqlConnection
        {
            Id = _activeConnection?.Id ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Server = server,
            BackupDirectory = BackupDirectoryBox.Text.Trim(),
            UseWindowsAuthentication = windowsAuth,
            Username = windowsAuth ? "" : username,
            ProtectedPassword = protectedPassword
        };
        return true;
    }

    private void SetBusy(bool busy)
    {
        SavedConnectionsCombo.IsEnabled = !busy;
        ServerBox.IsEnabled = !busy;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
