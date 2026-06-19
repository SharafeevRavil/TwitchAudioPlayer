using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System;
using System.Globalization;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder
{
    public class MusicOrderRepository
    {
        private readonly string _connectionString =
            $"Data Source={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TwitchAudioPlayer", "MusicOrders.sqlite")};Version=3;";

        public MusicOrderRepository()
        {
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            const string createTableQuery = @"
        CREATE TABLE IF NOT EXISTS MusicOrders (
            Uri TEXT NOT NULL,
            Date TEXT NOT NULL,
            Type INTEGER NOT NULL,
            Played INTEGER NOT NULL,
            IsActive BOOLEAN NOT NULL DEFAULT 1,
            PRIMARY KEY (Uri, Date, Type))";
            using var command = new SQLiteCommand(createTableQuery, connection);
            command.ExecuteNonQuery();
        }


        public List<MusicOrder> AddOrders(List<MusicOrder> orders)
        {
            // пихуй, все равно много не будет
            var addedOrders = new List<MusicOrder>();
            foreach (var order in orders)
            {
                if (AddOrder(order))
                    addedOrders.Add(order);
            }

            return addedOrders;
        }

        public bool AddOrder(MusicOrder order)
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            if (OrderExists(connection, order))
                return false;

            const string insertQuery = @"
        INSERT INTO MusicOrders (Uri, Date, Type, Played, IsActive)
        VALUES (@Uri, @Date, @Type, @Played, @IsActive)
        ON CONFLICT(Uri, Date, Type) DO NOTHING";

            using var command = new SQLiteCommand(insertQuery, connection);
            command.Parameters.AddWithValue("@Uri", order.Uri);
            command.Parameters.AddWithValue("@Date", ToStorageDate(order.Date));
            command.Parameters.AddWithValue("@Type", (int)order.Type);
            command.Parameters.AddWithValue("@Played", (int)order.Played);
            command.Parameters.AddWithValue("@IsActive", true);
            return command.ExecuteNonQuery() > 0;
        }


        public List<MusicOrder> GetValidOrders()
        {
            var orders = new List<MusicOrder>();
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            const int invalid = (int)Played.Invalid;
            var selectQuery = $"SELECT * FROM MusicOrders WHERE IsActive = 1 AND Played != {invalid}";
            using var command = new SQLiteCommand(selectQuery, connection);
            using var reader = command.ExecuteReader();
            while (reader.Read()) orders.Add(CreateOrder(reader));

            return orders;
        }

        public void MarkOrdersInactiveBefore(DateTimeOffset date, Played played)
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            const string updateQuery = "UPDATE MusicOrders SET IsActive = 0 WHERE Date < @Date AND Played = @Played";
            using var command = new SQLiteCommand(updateQuery, connection);
            command.Parameters.AddWithValue("@Date", ToStorageDate(date));
            command.Parameters.AddWithValue("@Played", (int)played);
            command.ExecuteNonQuery();
        }

        public int RestoreRecentInvalidOrders(DateTimeOffset since)
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            const string updateQuery = @"
        UPDATE MusicOrders
        SET Played = @NotPlayed
        WHERE IsActive = 1
          AND Played = @Invalid
          AND Date >= @Date";
            using var command = new SQLiteCommand(updateQuery, connection);
            command.Parameters.AddWithValue("@NotPlayed", (int)Played.NotPlayed);
            command.Parameters.AddWithValue("@Invalid", (int)Played.Invalid);
            command.Parameters.AddWithValue("@Date", ToStorageDate(since));
            return command.ExecuteNonQuery();
        }
        
        public bool MarkPlayed(MusicOrder order, Played played)
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            var updateQuery = $@"
        UPDATE MusicOrders
        SET Played = {(int)played}
        WHERE Uri = @Uri
          AND Type = @Type
          AND (Date = @Date OR Date = @RoundTripDate)";
            using var command = new SQLiteCommand(updateQuery, connection);
            command.Parameters.AddWithValue("@Uri", order.Uri);
            command.Parameters.AddWithValue("@Date", ToStorageDate(order.Date));
            command.Parameters.AddWithValue("@RoundTripDate", ToRoundTripDate(order.Date));
            command.Parameters.AddWithValue("@Type", (int)order.Type);
            return command.ExecuteNonQuery() > 0;
        }

        public MusicOrder? GetLastOrder(OrderType type)
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            const string selectQuery =
                "SELECT * FROM MusicOrders WHERE Type = @Type ORDER BY Date DESC LIMIT 1";
            using var command = new SQLiteCommand(selectQuery, connection);
            command.Parameters.AddWithValue("@Type", (int)type);
            using var reader = command.ExecuteReader();
            return reader.Read() ? CreateOrder(reader) : null; // Вернуть null, если заказов указанного типа не найдено
        }

        private static MusicOrder CreateOrder(SQLiteDataReader reader) =>
            new(
                reader["Uri"].ToString(),
                DateTimeOffset.Parse(reader["Date"].ToString(), CultureInfo.InvariantCulture),
                Enum.Parse<OrderType>(reader["Type"].ToString()),
                Enum.Parse<Played>(reader["Played"].ToString())
            );

        private static bool OrderExists(SQLiteConnection connection, MusicOrder order)
        {
            const string selectQuery = @"
        SELECT 1
        FROM MusicOrders
        WHERE Uri = @Uri
          AND Type = @Type
          AND (Date = @Date OR Date = @RoundTripDate)
        LIMIT 1";

            using var command = new SQLiteCommand(selectQuery, connection);
            command.Parameters.AddWithValue("@Uri", order.Uri);
            command.Parameters.AddWithValue("@Date", ToStorageDate(order.Date));
            command.Parameters.AddWithValue("@RoundTripDate", ToRoundTripDate(order.Date));
            command.Parameters.AddWithValue("@Type", (int)order.Type);
            return command.ExecuteScalar() != null;
        }

        private static string ToStorageDate(DateTimeOffset date) =>
            date.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

        private static string ToRoundTripDate(DateTimeOffset date) =>
            date.ToString("o", CultureInfo.InvariantCulture);
    }
}
