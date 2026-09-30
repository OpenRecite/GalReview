using System.Security.Cryptography;
using MySqlConnector;

public sealed class MySqlAuthRepository(AuthDatabase database) : IAuthRepository
{
    public RegistrationOutcome TryCreateCredential(Credential value)
    {
        try
        {
            using var connection = database.OpenConnection(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO auth_credentials (user_id,email,password_hash) VALUES (@id,@email,@hash);";
            command.Parameters.AddWithValue("@id", value.UserId); command.Parameters.AddWithValue("@email", value.Email); command.Parameters.AddWithValue("@hash", value.PasswordHash);
            command.ExecuteNonQuery(); return RegistrationOutcome.Created;
        }
        catch (MySqlException exception) when (exception.Number == 1062) { return RegistrationOutcome.EmailAlreadyRegistered; }
    }

    public RegistrationOutcome TryCreateCredentialWithInvitation(Credential value, string invitationCode)
    {
        using var connection = database.OpenConnection(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type,max_uses,used_count,valid_from,valid_to FROM admin_invitations WHERE code=@code LIMIT 1 FOR UPDATE;";
        command.Parameters.AddWithValue("@code", invitationCode);
        var foundInvitation = false;
        string? type = null;
        var maxUses = 0;
        var usedCount = 0;
        DateTime? validFrom = null;
        DateTime? validTo = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                foundInvitation = true;
                type = reader.GetString(0);
                maxUses = reader.GetInt32(1);
                usedCount = reader.GetInt32(2);
                validFrom = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
                validTo = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
            }
        }

        if (!foundInvitation) { transaction.Rollback(); return RegistrationOutcome.InvitationUnavailable; }
        var now = DateTime.UtcNow;
        var timeWindowIsValid = type != "time-window" || (validFrom is not null && validTo is not null && validFrom <= now && validTo >= now);
        var usageIsAvailable = type == "time-window" || usedCount < maxUses;
        if (!usageIsAvailable || !timeWindowIsValid) { transaction.Rollback(); return RegistrationOutcome.InvitationUnavailable; }
        try
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO auth_credentials (user_id,email,password_hash) VALUES (@id,@email,@hash);";
            command.Parameters.AddWithValue("@id", value.UserId); command.Parameters.AddWithValue("@email", value.Email); command.Parameters.AddWithValue("@hash", value.PasswordHash);
            command.ExecuteNonQuery();
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            transaction.Rollback();
            return RegistrationOutcome.EmailAlreadyRegistered;
        }
        command.Parameters.Clear();
        command.CommandText = "UPDATE admin_invitations SET used_count=used_count+1 WHERE code=@code AND (type='time-window' OR used_count<max_uses);";
        command.Parameters.AddWithValue("@code", invitationCode);
        if (command.ExecuteNonQuery() != 1) { transaction.Rollback(); return RegistrationOutcome.InvitationUnavailable; }
        transaction.Commit();
        return RegistrationOutcome.Created;
    }
    public void RollbackRegistration(string userId, string invitationCode)
    {
        using var connection = database.OpenConnection(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM auth_credentials WHERE user_id=@id;";
        command.Parameters.AddWithValue("@id", userId);
        var deleted = command.ExecuteNonQuery();
        if (deleted == 1)
        {
            command.Parameters.Clear();
            command.CommandText = "UPDATE admin_invitations SET used_count=used_count-1 WHERE code=@code AND used_count>0;";
            command.Parameters.AddWithValue("@code", invitationCode);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void DeleteCredential(string id) { using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="DELETE FROM auth_credentials WHERE user_id=@id;";q.Parameters.AddWithValue("@id",id);q.ExecuteNonQuery(); }
    public bool DeleteAccount(string userId)
    {
        using var connection = database.OpenConnection(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CAST(user_id AS CHAR) FROM auth_credentials WHERE user_id=@id LIMIT 1 FOR UPDATE;";
        command.Parameters.AddWithValue("@id", userId);
        if (command.ExecuteScalar() is null) { transaction.Rollback(); return false; }
        command.Parameters.Clear();
        command.CommandText = "DELETE FROM auth_sessions WHERE user_id=@id; DELETE FROM auth_password_resets WHERE user_id=@id; DELETE FROM admin_user_overrides WHERE user_id=@id; DELETE FROM auth_credentials WHERE user_id=@id;";
        command.Parameters.AddWithValue("@id", userId);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }
    public Credential? FindCredential(string email) { using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="SELECT CAST(user_id AS CHAR),email,password_hash FROM auth_credentials WHERE email=@email;";q.Parameters.AddWithValue("@email",email);using var r=q.ExecuteReader();return r.Read()?new Credential(DbText(r.GetValue(0)),r.GetString(1),r.GetString(2)):null; }
    public Credential? FindCredentialById(string userId) { using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="SELECT CAST(user_id AS CHAR),email,password_hash FROM auth_credentials WHERE user_id=@id;";q.Parameters.AddWithValue("@id",userId);using var r=q.ExecuteReader();return r.Read()?new Credential(DbText(r.GetValue(0)),r.GetString(1),r.GetString(2)):null; }
    public StoredSession CreateSession(string userId,string? deviceName) { var now=DateTimeOffset.UtcNow;var s=new StoredSession(Guid.NewGuid().ToString(),userId,Token(),Token(),now,now.AddMinutes(15),now.AddDays(7),null); InsertSession(s);return s; }
    public StoredSession? FindSession(string id) => FindSession("session_id",id,false); public StoredSession? FindByAccessToken(string token)=>FindSession("access_hash",Hash(token),true); public StoredSession? TouchAccessToken(string token) { var session=FindByAccessToken(token); var now=DateTimeOffset.UtcNow; return session is null||session.Status!="ACTIVE"||session.AccessExpiresAt<=now?null:session; }
    public bool RevokeSession(string sessionId,string userId) { using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="UPDATE auth_sessions SET revoked_at=UTC_TIMESTAMP(6) WHERE session_id=@id AND user_id=@user AND revoked_at IS NULL;";q.Parameters.AddWithValue("@id",sessionId);q.Parameters.AddWithValue("@user",userId);return q.ExecuteNonQuery()==1; }
    public StoredSession? Rotate(string refresh) { var previousHash=Hash(refresh); var old=FindSession("refresh_hash",previousHash,true); if(old is null||old.Status!="ACTIVE")return null; var now=DateTimeOffset.UtcNow; var access=Token(); var nextRefresh=Token(); var rotated=old with { AccessToken=access, RefreshToken=nextRefresh, AccessExpiresAt=now.AddMinutes(15), RefreshExpiresAt=now.AddDays(7) }; using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="UPDATE auth_sessions SET access_hash=@access,refresh_hash=@refresh,access_expires_at=@accessExpires,refresh_expires_at=@refreshExpires WHERE session_id=@id AND refresh_hash=@previousRefresh AND revoked_at IS NULL AND refresh_expires_at>UTC_TIMESTAMP(6);";q.Parameters.AddWithValue("@access",Hash(access));q.Parameters.AddWithValue("@refresh",Hash(nextRefresh));q.Parameters.AddWithValue("@accessExpires",rotated.AccessExpiresAt.UtcDateTime);q.Parameters.AddWithValue("@refreshExpires",rotated.RefreshExpiresAt.UtcDateTime);q.Parameters.AddWithValue("@id",old.SessionId);q.Parameters.AddWithValue("@previousRefresh",previousHash);return q.ExecuteNonQuery()==1?rotated:null; }
    public void RevokeAllSessions(string userId){using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="UPDATE auth_sessions SET revoked_at=UTC_TIMESTAMP(6) WHERE user_id=@id AND revoked_at IS NULL;";q.Parameters.AddWithValue("@id",userId);q.ExecuteNonQuery();}
    public void UpdatePassword(Credential credential){using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="UPDATE auth_credentials SET password_hash=@hash WHERE user_id=@id;";q.Parameters.AddWithValue("@id",credential.UserId);q.Parameters.AddWithValue("@hash",credential.PasswordHash);q.ExecuteNonQuery();}
    public string CreatePasswordReset(string userId)
    {
        var token = PasswordResetToken();
        var normalizedToken = token.ToUpperInvariant();
        using var c = database.OpenConnection();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "DELETE FROM auth_password_resets WHERE user_id=@user AND used_at IS NULL;";
        q.Parameters.AddWithValue("@user", userId);
        q.ExecuteNonQuery();
        q.Parameters.Clear();
        q.CommandText = "INSERT INTO auth_password_resets (token_hash,user_id,expires_at,used_at) VALUES (@hash,@user,@expires,NULL);";
        q.Parameters.AddWithValue("@hash", Hash(normalizedToken));
        q.Parameters.AddWithValue("@user", userId);
        q.Parameters.AddWithValue("@expires", DateTime.UtcNow.AddMinutes(10));
        q.ExecuteNonQuery();
        tx.Commit();
        return token;
    }
    public void DeletePasswordReset(string token){using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="DELETE FROM auth_password_resets WHERE token_hash=@hash AND used_at IS NULL;";q.Parameters.AddWithValue("@hash",Hash(token.ToUpperInvariant()));q.ExecuteNonQuery();}
    public Credential? ConsumePasswordReset(string token){if(string.IsNullOrWhiteSpace(token)||token.Length<6||token.Length>32)return null;using var c=database.OpenConnection();using var tx=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="SELECT CAST(user_id AS CHAR) FROM auth_password_resets WHERE token_hash=@hash AND used_at IS NULL AND expires_at>UTC_TIMESTAMP(6) LIMIT 1 FOR UPDATE;";q.Parameters.AddWithValue("@hash",Hash(token.ToUpperInvariant()));var rawUser=q.ExecuteScalar();var user=rawUser is null?null:DbText(rawUser);if(user is null){tx.Rollback();return null;}q.Parameters.Clear();q.CommandText="UPDATE auth_password_resets SET used_at=UTC_TIMESTAMP(6) WHERE token_hash=@hash;";q.Parameters.AddWithValue("@hash",Hash(token.ToUpperInvariant()));q.ExecuteNonQuery();tx.Commit();using var q2=c.CreateCommand();q2.CommandText="SELECT CAST(user_id AS CHAR),email,password_hash FROM auth_credentials WHERE user_id=@id;";q2.Parameters.AddWithValue("@id",user);using var r=q2.ExecuteReader();return r.Read()?new Credential(DbText(r.GetValue(0)),r.GetString(1),r.GetString(2)):null;}
    private void InsertSession(StoredSession s){using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="INSERT INTO auth_sessions (session_id,user_id,access_hash,refresh_hash,created_at,access_expires_at,refresh_expires_at,revoked_at) VALUES (@id,@user,@access,@refresh,@created,@accessExpires,@refreshExpires,NULL);";q.Parameters.AddWithValue("@id",s.SessionId);q.Parameters.AddWithValue("@user",s.UserId);q.Parameters.AddWithValue("@access",Hash(s.AccessToken));q.Parameters.AddWithValue("@refresh",Hash(s.RefreshToken));q.Parameters.AddWithValue("@created",s.CreatedAt.UtcDateTime);q.Parameters.AddWithValue("@accessExpires",s.AccessExpiresAt.UtcDateTime);q.Parameters.AddWithValue("@refreshExpires",s.RefreshExpiresAt.UtcDateTime);q.ExecuteNonQuery();}
    private StoredSession? FindSession(string column,string value,bool hashed){using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText=$"SELECT CAST(session_id AS CHAR),CAST(user_id AS CHAR),created_at,access_expires_at,refresh_expires_at,revoked_at FROM auth_sessions WHERE {column}=@value LIMIT 1;";q.Parameters.AddWithValue("@value",value);using var r=q.ExecuteReader();return r.Read()?new StoredSession(DbText(r.GetValue(0)),DbText(r.GetValue(1)),string.Empty,string.Empty,AsUtc(r.GetDateTime(2)),AsUtc(r.GetDateTime(3)),AsUtc(r.GetDateTime(4)),r.IsDBNull(5)?null:AsUtc(r.GetDateTime(5))):null;}
    private static string Token()=>Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+','-').Replace('/','_').TrimEnd('='); private static string PasswordResetToken()=>GenerateResetToken(); private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));private static string DbText(object value)=>value is Guid guid?guid.ToString():Convert.ToString(value)!;private static DateTimeOffset AsUtc(DateTime value)=>new(DateTime.SpecifyKind(value,DateTimeKind.Utc));
    private const string ResetTokenAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // gitleaks:allow 验证码字符表（非密钥）
    private static string GenerateResetToken()
    {
        // Reject the top 16 byte values before modulo reduction so every one of
        // the 30 symbols is selected with equal probability.
        var chars = new char[8];
        Span<byte> bytes = stackalloc byte[16];
        var written = 0;
        while (written < chars.Length)
        {
            RandomNumberGenerator.Fill(bytes);
            foreach (var value in bytes)
            {
                if (value >= 240) continue;
                chars[written++] = ResetTokenAlphabet[value % ResetTokenAlphabet.Length];
                if (written == chars.Length) break;
            }
        }
        return new string(chars);
    }
}

public sealed class MySqlAdminRepository(AuthDatabase database) : IAdminRepository
{
    public List<AdminAccount> ListUsers()
    {
        using var c = database.OpenConnection(); using var q = c.CreateCommand();
        q.CommandText = "SELECT CAST(user_id AS CHAR), email FROM auth_credentials ORDER BY email;";
        using var r = q.ExecuteReader(); var result = new List<AdminAccount>();
        while (r.Read()) result.Add(new AdminAccount(DbText(r.GetValue(0)), DbText(r.GetValue(1))));
        return result;
    }
    public bool UserExists(string userId)
    {
        using var c = database.OpenConnection(); using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM auth_credentials WHERE user_id=@id;";
        q.Parameters.AddWithValue("@id", userId);
        return Convert.ToInt32(q.ExecuteScalar()) == 1;
    }
    public bool DeleteAuthUser(string userId)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT COUNT(*) FROM auth_credentials WHERE user_id=@id;";q.Parameters.AddWithValue("@id",userId);if(Convert.ToInt32(q.ExecuteScalar())==0){tx.Rollback();return false;}
        q.Parameters.Clear();q.CommandText="DELETE FROM auth_sessions WHERE user_id=@id; DELETE FROM auth_password_resets WHERE user_id=@id; DELETE FROM admin_user_overrides WHERE user_id=@id; DELETE FROM auth_credentials WHERE user_id=@id;";q.Parameters.AddWithValue("@id",userId);q.ExecuteNonQuery();tx.Commit();return true;
    }
    public List<AdminInvitation> ListInvitations()
    {
        using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="SELECT code,type,max_uses,used_count,valid_from,valid_to,created_at FROM admin_invitations ORDER BY created_at DESC;";using var r=q.ExecuteReader();var result=new List<AdminInvitation>();
        while(r.Read())result.Add(new AdminInvitation(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3),r.IsDBNull(4)?null:Utc(r.GetDateTime(4)),r.IsDBNull(5)?null:Utc(r.GetDateTime(5)),Utc(r.GetDateTime(6))));return result;
    }
    public AdminInvitation? CreateInvitation(CreateInvitationRequest request)
    {
        var type=request.Type?.Trim().ToLowerInvariant();
        if(type is not ("single-use" or "multi-use" or "time-window")||(type=="multi-use"&&!request.MaxUses.HasValue))return null;
        var max=type switch{"single-use"=>1,"time-window"=>int.MaxValue,_=>request.MaxUses.GetValueOrDefault()};
        if((type=="multi-use"&&max is <1 or >10000)||(type=="time-window"&&(!request.ValidFrom.HasValue||!request.ValidTo.HasValue||request.ValidTo<=request.ValidFrom)))return null;
        for(var i=0;i<3;i++){var value=new AdminInvitation("MS-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(5)),type,max,0,request.ValidFrom,request.ValidTo,DateTimeOffset.UtcNow);try{using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="INSERT INTO admin_invitations (code,type,max_uses,used_count,valid_from,valid_to,created_at) VALUES (@code,@type,@max,0,@from,@to,@created);";q.Parameters.AddWithValue("@code",value.Code);q.Parameters.AddWithValue("@type",value.Type);q.Parameters.AddWithValue("@max",value.MaxUses);q.Parameters.AddWithValue("@from",value.ValidFrom?.UtcDateTime??(object)DBNull.Value);q.Parameters.AddWithValue("@to",value.ValidTo?.UtcDateTime??(object)DBNull.Value);q.Parameters.AddWithValue("@created",value.CreatedAt.UtcDateTime);q.ExecuteNonQuery();return value;}catch(MySqlException ex)when(ex.Number==1062){}}return null;
    }
    public bool DeleteInvitation(string code){using var c=database.OpenConnection();using var q=c.CreateCommand();q.CommandText="DELETE FROM admin_invitations WHERE code=@code;";q.Parameters.AddWithValue("@code",code);return q.ExecuteNonQuery()==1;}
    private static string DbText(object value)=>value is Guid guid?guid.ToString():Convert.ToString(value)!;private static DateTimeOffset Utc(DateTime value)=>new(DateTime.SpecifyKind(value,DateTimeKind.Utc));
}

public sealed class MySqlAdminAuditRepository(AuthDatabase database) : IAdminAuditRepository
{
    public void Write(AdminAuditRecord record)
    {
        using var connection = database.OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO admin_audit_logs (audit_id,actor_user_id,action,target_user_id,target_invitation_code,outcome,trace_id,created_at) VALUES (@auditId,@actorUserId,@action,@targetUserId,@targetInvitationCode,@outcome,@traceId,@createdAt);";
        command.Parameters.AddWithValue("@auditId", record.AuditId);
        command.Parameters.AddWithValue("@actorUserId", record.ActorUserId);
        command.Parameters.AddWithValue("@action", record.Action);
        command.Parameters.AddWithValue("@targetUserId", record.TargetUserId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@targetInvitationCode", record.TargetInvitationCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@outcome", record.Outcome);
        command.Parameters.AddWithValue("@traceId", record.TraceId);
        command.Parameters.AddWithValue("@createdAt", record.CreatedAt.UtcDateTime);
        command.ExecuteNonQuery();
    }
}
