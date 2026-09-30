using System.Text.Json;
using MySqlConnector;

public sealed class UserDatabase(string connectionString)
{
    private readonly string _connectionString = connectionString;

    public MySqlConnection OpenConnection()
    {
        var connection = new MySqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void EnsureCreated()
    {
        var builder = new MySqlConnectionStringBuilder(_connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database)
            || !builder.Database.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new InvalidOperationException("Invalid MySQL database name.");
        var database = builder.Database;
        builder.Database = string.Empty;
        using (var server = new MySqlConnection(builder.ConnectionString))
        {
            server.Open();
            using var create = server.CreateCommand();
            create.CommandText = $"CREATE DATABASE IF NOT EXISTS `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;";
            create.ExecuteNonQuery();
        }
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS user_profiles (
              user_id CHAR(36) PRIMARY KEY, display_name VARCHAR(64) NOT NULL,
              avatar_url VARCHAR(2048) NULL, locale VARCHAR(16) NOT NULL,
              preferred_subject_codes JSON NOT NULL, created_at DATETIME(6) NOT NULL,
              updated_at DATETIME(6) NOT NULL
            );
            CREATE TABLE IF NOT EXISTS user_preferences (
              user_id CHAR(36) PRIMARY KEY, daily_goal_minutes INT NOT NULL,
              content_difficulty VARCHAR(16) NOT NULL, reduced_motion BOOLEAN NOT NULL,
              updated_at DATETIME(6) NOT NULL,
              CONSTRAINT fk_preferences_profile FOREIGN KEY (user_id) REFERENCES user_profiles(user_id) ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();
    }
}

public sealed class MySqlUserRepository(UserDatabase database) : IUserRepository
{
    public bool TryCreate(UserProfile profile)
    {
        try
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO user_profiles (user_id, display_name, avatar_url, locale, preferred_subject_codes, created_at, updated_at) VALUES (@id, @displayName, NULL, @locale, @subjects, @created, @updated);";
            command.Parameters.AddWithValue("@id", profile.UserId);
            command.Parameters.AddWithValue("@displayName", profile.DisplayName);
            command.Parameters.AddWithValue("@locale", profile.Locale);
            command.Parameters.AddWithValue("@subjects", JsonSerializer.Serialize(profile.PreferredSubjectCodes));
            command.Parameters.AddWithValue("@created", profile.CreatedAt.UtcDateTime);
            command.Parameters.AddWithValue("@updated", profile.UpdatedAt.UtcDateTime);
            return command.ExecuteNonQuery() == 1;
        }
        catch (MySqlException ex) when (ex.Number == 1062) { return false; }
    }

    public UserProfile? FindProfile(string userId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(user_id AS CHAR), display_name, avatar_url, locale, preferred_subject_codes, created_at, updated_at FROM user_profiles WHERE user_id = @id;";
        command.Parameters.AddWithValue("@id", userId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    public List<AdminProfileSummary> FindAdminProfiles(IReadOnlyList<string> userIds)
    {
        if (userIds.Count == 0) return [];
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        var parameterNames = new List<string>(userIds.Count);
        for (var index = 0; index < userIds.Count; index++)
        {
            var parameterName = $"@id{index}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, userIds[index]);
        }
        command.CommandText = $"SELECT CAST(user_id AS CHAR), display_name FROM user_profiles WHERE user_id IN ({string.Join(',', parameterNames)});";
        using var reader = command.ExecuteReader();
        var result = new List<AdminProfileSummary>();
        while (reader.Read())
            result.Add(new AdminProfileSummary(DbText(reader.GetValue(0)), reader.GetString(1)));
        return result;
    }

    public bool DeleteProfile(string userId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM user_profiles WHERE user_id=@id;";
        command.Parameters.AddWithValue("@id", userId);
        return command.ExecuteNonQuery() == 1;
    }

    public UserProfile? UpdateProfile(string userId, UpdateUserProfileRequest request)
    {
        var current = FindProfile(userId);
        if (current is null) return null;
        var updated = current with
        {
            DisplayName = request.DisplayName?.Trim() ?? current.DisplayName,
            Locale = request.Locale?.Trim() ?? current.Locale,
            PreferredSubjectCodes = request.PreferredSubjectCodes ?? current.PreferredSubjectCodes,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE user_profiles SET display_name=@name, locale=@locale, preferred_subject_codes=@subjects, updated_at=@updated WHERE user_id=@id;";
        command.Parameters.AddWithValue("@id", updated.UserId);
        command.Parameters.AddWithValue("@name", updated.DisplayName);
        command.Parameters.AddWithValue("@locale", updated.Locale);
        command.Parameters.AddWithValue("@subjects", JsonSerializer.Serialize(updated.PreferredSubjectCodes));
        command.Parameters.AddWithValue("@updated", updated.UpdatedAt.UtcDateTime);
        if (command.ExecuteNonQuery() != 1) return null;
        return updated;
    }

    public UserPreferences? FindPreferences(string userId)
    {
        if (FindProfile(userId) is null) return null;
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT daily_goal_minutes, content_difficulty, reduced_motion, updated_at FROM user_preferences WHERE user_id=@id;";
        command.Parameters.AddWithValue("@id", userId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2), AsUtc(reader.GetDateTime(3)))
            : new UserPreferences(30, "STANDARD", false, DateTimeOffset.UtcNow);
    }

    public UserPreferences? ReplacePreferences(string userId, UserPreferencesInput request)
    {
        if (FindProfile(userId) is null) return null;
        var result = new UserPreferences(request.DailyGoalMinutes, request.ContentDifficulty, request.ReducedMotion, DateTimeOffset.UtcNow);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO user_preferences (user_id, daily_goal_minutes, content_difficulty, reduced_motion, updated_at) VALUES (@id,@goal,@difficulty,@motion,@updated) ON DUPLICATE KEY UPDATE daily_goal_minutes=@goal, content_difficulty=@difficulty, reduced_motion=@motion, updated_at=@updated;";
        command.Parameters.AddWithValue("@id", userId);
        command.Parameters.AddWithValue("@goal", result.DailyGoalMinutes);
        command.Parameters.AddWithValue("@difficulty", result.ContentDifficulty);
        command.Parameters.AddWithValue("@motion", result.ReducedMotion);
        command.Parameters.AddWithValue("@updated", result.UpdatedAt.UtcDateTime);
        try { command.ExecuteNonQuery(); }
        catch (MySqlException failure) when (failure.Number == 1452) { return null; }
        return result;
    }

    private static UserProfile ReadProfile(MySqlDataReader r) =>
        new(DbText(r.GetValue(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
            r.GetString(3), JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? [],
            AsUtc(r.GetDateTime(5)), AsUtc(r.GetDateTime(6)));

    private static string DbText(object value) => value is Guid guid ? guid.ToString() : Convert.ToString(value)!;
    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
