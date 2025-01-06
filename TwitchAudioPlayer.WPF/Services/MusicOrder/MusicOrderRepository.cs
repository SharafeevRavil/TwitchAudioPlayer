using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System;

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


        public void AddOrders(List<MusicOrder> orders)
        {
            // пихуй, все равно много не будет
            foreach (var order in orders) AddOrder(order);
        }

        public void AddOrder(MusicOrder order)
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();

            const string insertQuery = @"
        INSERT INTO MusicOrders (Uri, Date, Type, Played, IsActive) 
        VALUES (@Uri, @Date, @Type, @Played, @IsActive)
        ON CONFLICT(Uri, Date, Type) DO NOTHING";

            using var command = new SQLiteCommand(insertQuery, connection);
            command.Parameters.AddWithValue("@Uri", order.Uri);
            command.Parameters.AddWithValue("@Date", order.Date.ToString("o"));
            command.Parameters.AddWithValue("@Type", (int)order.Type);
            command.Parameters.AddWithValue("@Played", (int)order.Played);
            command.Parameters.AddWithValue("@IsActive", true);
            command.ExecuteNonQuery();
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
            command.Parameters.AddWithValue("@Date", date.ToString("o"));
            command.Parameters.AddWithValue("@Played", (int)played);
            command.ExecuteNonQuery();
        }
        
        public void MarkPlayed(MusicOrder order, Played played)
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            var updateQuery = $"UPDATE MusicOrders SET Played = {(int)played} WHERE Uri = @Uri AND Date = @Date AND Type = @Type";
            using var command = new SQLiteCommand(updateQuery, connection);
            command.Parameters.AddWithValue("@Uri", order.Uri);
            command.Parameters.AddWithValue("@Date", order.Date.ToString("o"));
            command.Parameters.AddWithValue("@Type", (int)order.Type);
            command.ExecuteNonQuery();
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
                DateTimeOffset.Parse(reader["Date"].ToString()),
                Enum.Parse<OrderType>(reader["Type"].ToString()),
                Enum.Parse<Played>(reader["Played"].ToString())
            );
    }
}