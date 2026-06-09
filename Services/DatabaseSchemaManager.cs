using System.Data.Common;

namespace OpenCredential.AdminWeb.Services;

internal static class DatabaseSchemaManager
{
    public static void ApplySchema(DbConnection connection, bool isPostgreSql)
    {
        foreach (var sql in GetSchemaStatements(isPostgreSql))
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static IEnumerable<string> GetSchemaStatements(bool isPostgreSql)
    {
        if (isPostgreSql)
        {
            return new[]
            {
                """
                CREATE TABLE IF NOT EXISTS "careers" (
                  "id" INT PRIMARY KEY,
                  "name" VARCHAR(255) NOT NULL,
                  "status" INT NOT NULL DEFAULT 1
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "levels" (
                  "id" INT PRIMARY KEY,
                  "name" VARCHAR(100) NOT NULL,
                  "status" INT NOT NULL DEFAULT 1
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "users" (
                  "id" INT NOT NULL,
                  "username" VARCHAR(50) NOT NULL,
                  "first_name" VARCHAR(100),
                  "last_name" VARCHAR(100),
                  "document_id" VARCHAR(15),
                  "email" VARCHAR(200),
                  "status" INT NOT NULL DEFAULT 1,
                  "career_id" INT NULL,
                  "level_id" INT NULL,
                  "hash_method" TEXT NOT NULL DEFAULT 'NONE',
                  "password_hash" TEXT NULL,
                  "failed_attempts" INT NOT NULL DEFAULT 0,
                  "locked_until" TIMESTAMP NULL,
                  "last_attempt_at" TIMESTAMP NULL,
                  CONSTRAINT "pk_users_id" PRIMARY KEY ("id"),
                  CONSTRAINT "uq_users_username" UNIQUE ("username")
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "computers" (
                  "id" INT PRIMARY KEY,
                  "name" VARCHAR(128) NOT NULL,
                  "location" VARCHAR(150) NOT NULL,
                  "inventory_tag" VARCHAR(80) NOT NULL,
                  "ip_address" VARCHAR(45) NULL,
                  "status" VARCHAR(20) NOT NULL,
                  "current_username" VARCHAR(128) NULL,
                  "last_seen_utc" TIMESTAMP NOT NULL
                )
                """,
                """
                ALTER TABLE "computers" ADD COLUMN IF NOT EXISTS "ip_address" VARCHAR(45) NULL
                """,
                """
                ALTER TABLE "login_sessions" ADD COLUMN IF NOT EXISTS "client_session_id" VARCHAR(100) NULL
                """,
                """
                ALTER TABLE "login_sessions" ADD COLUMN IF NOT EXISTS "windows_session_id" INT NULL
                """,
                """
                ALTER TABLE "login_sessions" ADD COLUMN IF NOT EXISTS "session_state" VARCHAR(30) NULL
                """,
                """
                ALTER TABLE "login_sessions" ADD COLUMN IF NOT EXISTS "last_heartbeat_at" TIMESTAMP NULL
                """,
                """
                ALTER TABLE "login_sessions" ADD COLUMN IF NOT EXISTS "session_end_reason" VARCHAR(50) NULL
                """,
                """
                ALTER TABLE "login_sessions" ADD COLUMN IF NOT EXISTS "session_origin" VARCHAR(30) NULL
                """,
                """
                CREATE TABLE IF NOT EXISTS "rooms" (
                  "id" INT PRIMARY KEY,
                  "name" VARCHAR(150) NOT NULL,
                  "code" VARCHAR(50) NOT NULL,
                  "canvas_width" INT NOT NULL DEFAULT 1200,
                  "canvas_height" INT NOT NULL DEFAULT 720,
                  "status" INT NOT NULL DEFAULT 1
                )
                """,
                """
                ALTER TABLE "rooms" ADD COLUMN IF NOT EXISTS "canvas_width" INT NOT NULL DEFAULT 1200
                """,
                """
                ALTER TABLE "rooms" ADD COLUMN IF NOT EXISTS "canvas_height" INT NOT NULL DEFAULT 720
                """,
                """
                CREATE TABLE IF NOT EXISTS "room_positions" (
                  "id" INT PRIMARY KEY,
                  "room_id" INT NOT NULL,
                  "label" VARCHAR(80) NOT NULL,
                  "item_type" VARCHAR(30) NOT NULL DEFAULT 'Computer',
                  "pos_x" INT NULL,
                  "pos_y" INT NULL,
                  "item_width" INT NULL,
                  "item_height" INT NULL,
                  "item_orientation" VARCHAR(20) NOT NULL DEFAULT 'Horizontal',
                  "seat_capacity" INT NOT NULL DEFAULT 1,
                  "row_number" INT NOT NULL,
                  "column_number" INT NOT NULL,
                  "computer_id" INT NULL
                )
                """,
                """
                ALTER TABLE "room_positions" ADD COLUMN IF NOT EXISTS "item_type" VARCHAR(30) NOT NULL DEFAULT 'Computer'
                """,
                """
                ALTER TABLE "room_positions" ADD COLUMN IF NOT EXISTS "pos_x" INT NULL
                """,
                """
                ALTER TABLE "room_positions" ADD COLUMN IF NOT EXISTS "pos_y" INT NULL
                """,
                """
                ALTER TABLE "room_positions" ADD COLUMN IF NOT EXISTS "item_width" INT NULL
                """,
                """
                ALTER TABLE "room_positions" ADD COLUMN IF NOT EXISTS "item_height" INT NULL
                """,
                """
                ALTER TABLE "room_positions" ADD COLUMN IF NOT EXISTS "item_orientation" VARCHAR(20) NOT NULL DEFAULT 'Horizontal'
                """,
                """
                ALTER TABLE "room_positions" ADD COLUMN IF NOT EXISTS "seat_capacity" INT NOT NULL DEFAULT 1
                """,
                """
                ALTER TABLE "room_positions" ADD COLUMN IF NOT EXISTS "row_number" INT NOT NULL DEFAULT 1
                """,
                """
                ALTER TABLE "room_positions" ADD COLUMN IF NOT EXISTS "column_number" INT NOT NULL DEFAULT 1
                """,
                """
                CREATE TABLE IF NOT EXISTS "usage_records" (
                  "id" INT PRIMARY KEY,
                  "user_id" INT NOT NULL,
                  "computer_id" INT NOT NULL,
                  "start_utc" TIMESTAMP NOT NULL,
                  "end_utc" TIMESTAMP NOT NULL
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "admin_audit_log" (
                  "id" INT PRIMARY KEY,
                  "actor_username" VARCHAR(100) NOT NULL,
                  "action" VARCHAR(60) NOT NULL,
                  "entity_type" VARCHAR(80) NOT NULL,
                  "entity_key" VARCHAR(120) NOT NULL,
                  "summary" TEXT NOT NULL,
                  "remote_ip" VARCHAR(64) NULL,
                  "created_utc" TIMESTAMP NOT NULL
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "portal_password_reset_tokens" (
                  "id" INT PRIMARY KEY,
                  "user_id" INT NULL,
                  "username" VARCHAR(50) NOT NULL,
                  "email" VARCHAR(200) NOT NULL,
                  "reset_token" VARCHAR(120) NOT NULL,
                  "created_utc" TIMESTAMP NOT NULL,
                  "expires_utc" TIMESTAMP NOT NULL,
                  "consumed_utc" TIMESTAMP NULL
                )
                """,
                """
                ALTER TABLE "portal_password_reset_tokens" ADD COLUMN IF NOT EXISTS "user_id" INT NULL
                """,
                """
                ALTER TABLE "portal_password_reset_tokens" ADD COLUMN IF NOT EXISTS "username" VARCHAR(50) NOT NULL DEFAULT ''
                """,
                """
                ALTER TABLE "portal_password_reset_tokens" ADD COLUMN IF NOT EXISTS "email" VARCHAR(200) NOT NULL DEFAULT ''
                """,
                """
                ALTER TABLE "portal_password_reset_tokens" ADD COLUMN IF NOT EXISTS "reset_token" VARCHAR(120) NOT NULL DEFAULT ''
                """,
                """
                ALTER TABLE "portal_password_reset_tokens" ADD COLUMN IF NOT EXISTS "created_utc" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                """,
                """
                ALTER TABLE "portal_password_reset_tokens" ADD COLUMN IF NOT EXISTS "expires_utc" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                """,
                """
                ALTER TABLE "portal_password_reset_tokens" ADD COLUMN IF NOT EXISTS "consumed_utc" TIMESTAMP NULL
                """
            };
        }

        return new[]
        {
            """
            CREATE TABLE IF NOT EXISTS `careers` (
              `id` INT NOT NULL,
              `name` VARCHAR(255) NOT NULL,
              `status` INT NOT NULL DEFAULT 1,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS `levels` (
              `id` INT NOT NULL,
              `name` VARCHAR(100) NOT NULL,
              `status` INT NOT NULL DEFAULT 1,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS `users` (
              `id` INT NOT NULL,
              `username` VARCHAR(50) NOT NULL,
              `first_name` VARCHAR(100) NULL,
              `last_name` VARCHAR(100) NULL,
              `document_id` VARCHAR(15) NULL,
              `email` VARCHAR(200) NULL,
              `status` INT NOT NULL DEFAULT 1,
              `career_id` INT NULL,
              `level_id` INT NULL,
              `hash_method` TEXT NOT NULL,
              `password_hash` TEXT NULL,
              `failed_attempts` INT NOT NULL DEFAULT 0,
              `locked_until` DATETIME NULL,
              `last_attempt_at` DATETIME NULL,
              PRIMARY KEY (`id`),
              UNIQUE KEY `uq_users_username` (`username`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS `computers` (
              `id` INT NOT NULL,
              `name` VARCHAR(128) NOT NULL,
              `location` VARCHAR(150) NOT NULL,
              `inventory_tag` VARCHAR(80) NOT NULL,
              `ip_address` VARCHAR(45) NULL,
              `status` VARCHAR(20) NOT NULL,
              `current_username` VARCHAR(128) NULL,
              `last_seen_utc` DATETIME NOT NULL,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            ALTER TABLE `computers` ADD COLUMN IF NOT EXISTS `ip_address` VARCHAR(45) NULL
            """,
            """
            ALTER TABLE `login_sessions` ADD COLUMN IF NOT EXISTS `client_session_id` VARCHAR(100) NULL
            """,
            """
            ALTER TABLE `login_sessions` ADD COLUMN IF NOT EXISTS `windows_session_id` INT NULL
            """,
            """
            ALTER TABLE `login_sessions` ADD COLUMN IF NOT EXISTS `session_state` VARCHAR(30) NULL
            """,
            """
            ALTER TABLE `login_sessions` ADD COLUMN IF NOT EXISTS `last_heartbeat_at` DATETIME NULL
            """,
            """
            ALTER TABLE `login_sessions` ADD COLUMN IF NOT EXISTS `session_end_reason` VARCHAR(50) NULL
            """,
            """
            ALTER TABLE `login_sessions` ADD COLUMN IF NOT EXISTS `session_origin` VARCHAR(30) NULL
            """,
            """
            CREATE TABLE IF NOT EXISTS `rooms` (
              `id` INT NOT NULL,
              `name` VARCHAR(150) NOT NULL,
              `code` VARCHAR(50) NOT NULL,
              `canvas_width` INT NOT NULL DEFAULT 1200,
              `canvas_height` INT NOT NULL DEFAULT 720,
              `status` INT NOT NULL DEFAULT 1,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            ALTER TABLE `rooms` ADD COLUMN IF NOT EXISTS `canvas_width` INT NOT NULL DEFAULT 1200
            """,
            """
            ALTER TABLE `rooms` ADD COLUMN IF NOT EXISTS `canvas_height` INT NOT NULL DEFAULT 720
            """,
            """
            CREATE TABLE IF NOT EXISTS `room_positions` (
              `id` INT NOT NULL,
              `room_id` INT NOT NULL,
              `label` VARCHAR(80) NOT NULL,
              `item_type` VARCHAR(30) NOT NULL DEFAULT 'Computer',
              `pos_x` INT NULL,
              `pos_y` INT NULL,
              `item_width` INT NULL,
              `item_height` INT NULL,
              `item_orientation` VARCHAR(20) NOT NULL DEFAULT 'Horizontal',
              `seat_capacity` INT NOT NULL DEFAULT 1,
              `row_number` INT NOT NULL,
              `column_number` INT NOT NULL,
              `computer_id` INT NULL,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            ALTER TABLE `room_positions` ADD COLUMN IF NOT EXISTS `item_type` VARCHAR(30) NOT NULL DEFAULT 'Computer'
            """,
            """
            ALTER TABLE `room_positions` ADD COLUMN IF NOT EXISTS `pos_x` INT NULL
            """,
            """
            ALTER TABLE `room_positions` ADD COLUMN IF NOT EXISTS `pos_y` INT NULL
            """,
            """
            ALTER TABLE `room_positions` ADD COLUMN IF NOT EXISTS `item_width` INT NULL
            """,
            """
            ALTER TABLE `room_positions` ADD COLUMN IF NOT EXISTS `item_height` INT NULL
            """,
            """
            ALTER TABLE `room_positions` ADD COLUMN IF NOT EXISTS `item_orientation` VARCHAR(20) NOT NULL DEFAULT 'Horizontal'
            """,
            """
            ALTER TABLE `room_positions` ADD COLUMN IF NOT EXISTS `seat_capacity` INT NOT NULL DEFAULT 1
            """,
            """
            ALTER TABLE `room_positions` ADD COLUMN IF NOT EXISTS `row_number` INT NOT NULL DEFAULT 1
            """,
            """
            ALTER TABLE `room_positions` ADD COLUMN IF NOT EXISTS `column_number` INT NOT NULL DEFAULT 1
            """,
            """
            CREATE TABLE IF NOT EXISTS `usage_records` (
              `id` INT NOT NULL,
              `user_id` INT NOT NULL,
              `computer_id` INT NOT NULL,
              `start_utc` DATETIME NOT NULL,
              `end_utc` DATETIME NOT NULL,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS `admin_audit_log` (
              `id` INT NOT NULL,
              `actor_username` VARCHAR(100) NOT NULL,
              `action` VARCHAR(60) NOT NULL,
              `entity_type` VARCHAR(80) NOT NULL,
              `entity_key` VARCHAR(120) NOT NULL,
              `summary` TEXT NOT NULL,
              `remote_ip` VARCHAR(64) NULL,
              `created_utc` DATETIME NOT NULL,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS `portal_password_reset_tokens` (
              `id` INT NOT NULL,
              `user_id` INT NULL,
              `username` VARCHAR(50) NOT NULL,
              `email` VARCHAR(200) NOT NULL,
              `reset_token` VARCHAR(120) NOT NULL,
              `created_utc` DATETIME NOT NULL,
              `expires_utc` DATETIME NOT NULL,
              `consumed_utc` DATETIME NULL,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            ALTER TABLE `portal_password_reset_tokens` ADD COLUMN IF NOT EXISTS `user_id` INT NULL
            """,
            """
            ALTER TABLE `portal_password_reset_tokens` ADD COLUMN IF NOT EXISTS `username` VARCHAR(50) NOT NULL DEFAULT ''
            """,
            """
            ALTER TABLE `portal_password_reset_tokens` ADD COLUMN IF NOT EXISTS `email` VARCHAR(200) NOT NULL DEFAULT ''
            """,
            """
            ALTER TABLE `portal_password_reset_tokens` ADD COLUMN IF NOT EXISTS `reset_token` VARCHAR(120) NOT NULL DEFAULT ''
            """,
            """
            ALTER TABLE `portal_password_reset_tokens` ADD COLUMN IF NOT EXISTS `created_utc` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
            """,
            """
            ALTER TABLE `portal_password_reset_tokens` ADD COLUMN IF NOT EXISTS `expires_utc` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
            """,
            """
            ALTER TABLE `portal_password_reset_tokens` ADD COLUMN IF NOT EXISTS `consumed_utc` DATETIME NULL
            """
        };
    }
}
