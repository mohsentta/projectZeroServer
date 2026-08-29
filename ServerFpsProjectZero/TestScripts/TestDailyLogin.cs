using ServerFpsProjectZero.Networking;
using System;
using System.Threading.Tasks;

public class TestDailyLogin : IDisposable
{
    private readonly LoginManager loginMgr;
    private string testUsername = "daily_test";

    public TestDailyLogin()
    {
        var server = new ServerManager();
        loginMgr = new LoginManager(server, new GameManager(), @"players.db");
        // Ensure player exists
        using (var conn = new SqliteConnection(loginMgr.connectionString))
        {
            conn.open();
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT OR REPLACE INTO Players (
                    PlayerId, Username, Email, PasswordHash, Salt,
                    Level, Experience, Gold, MMR, Rank, LastLogin, LoginStreak
                ) VALUES (
                    1, @Username, 'test@daily.com',
                    HashPassword('pass', GenerateSalt()), @Salt,
                    1, 0, 500, 1000, 1, datetime('now','utc'), 0)";
            cmd.Parameters.AddWithValue("@Username", testUsername);
            cmd.Parameters.AddWithValue("@Salt", GenerateSalt());
            cmd.ExecuteNonQuery();
        }
    }

    public async Task RunTests()
    {
        // 1️⃣ First login → streak = 1
        await LoginAndCheckStreak(1, "first_login");

        // 2️⃣ Consecutive logins (within 48 h) → streak increments
        for (int i = 0; i < 6; ++i)
            await LoginAndCheckStreak(i + 2, $"consecutive_{i}");

        // 3️⃣ Milestone reward check (7th day)
        await LoginAndCheckStreak(7, "milestone");

        // 4️⃣ Skip login → reset to 1
        await Task.Delay(TimeSpan.FromHours(25)); // >48 h window
        await LoginAndCheckStreak(1, "reset_after_skip");
    }

    private async Task LoginAndCheckStreak(int expectedStreak, string description)
    {
        // NOTE: This simulation relies on the ability of the test script to communicate with the server context.
        var loginResp = JsonConvert.DeserializeObject<LoginResponse>(
            serverManager.SendPacket(new LoginRequest { username = testUsername,
                                                        password = "pass" },
                                      new IPEndPoint(IPAddress.Loopback, 0)));

        // Simulate DB read after login (already done in HandleLogin)
        var profile = GetPlayerProfileFromDb(testUsername);
        Console.WriteLine($"{description}: Streak={profile.LoginStreak} (expected {expectedStreak})");
        if (profile.LoginStreak != expectedStreak) throw new Exception($"Streak mismatch for {description}");
    }

    private Player GetPlayerProfileFromDb(string username)
    {
        using (var conn = new SqliteConnection(loginMgr.connectionString))
        {
            conn.open();
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT LoginStreak FROM Players WHERE Username = @U";
            cmd.Parameters.AddWithValue("@U", username);
            return new Player { LoginStreak = (int)cmd.ExecuteScalar() };
        }
    }
}