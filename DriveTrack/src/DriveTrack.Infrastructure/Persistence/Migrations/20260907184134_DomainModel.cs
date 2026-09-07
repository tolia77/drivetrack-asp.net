using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveTrack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DomainModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asp_net_roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clients",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vehicles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    license_plate = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    capacity_kg = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    mileage = table.Column<int>(type: "integer", nullable: false),
                    next_maintenance_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vehicles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_role_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_asp_net_role_claims_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "asp_net_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_user_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_asp_net_user_claims_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_user_logins",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_asp_net_user_logins_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_user_roles",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    role_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_asp_net_user_roles_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "asp_net_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asp_net_user_roles_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_user_tokens",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_asp_net_user_tokens_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "drivers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    license_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    vehicle_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drivers", x => x.id);
                    table.ForeignKey(
                        name: "fk_drivers_vehicles_vehicle_id",
                        column: x => x.vehicle_id,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    client_id = table.Column<int>(type: "integer", nullable: true),
                    driver_id = table.Column<int>(type: "integer", nullable: true),
                    pickup_latitude = table.Column<double>(type: "double precision", nullable: false),
                    pickup_longitude = table.Column<double>(type: "double precision", nullable: false),
                    pickup_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    pickup_address_resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dropoff_latitude = table.Column<double>(type: "double precision", nullable: false),
                    dropoff_longitude = table.Column<double>(type: "double precision", nullable: false),
                    dropoff_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    dropoff_address_resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    package_details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    package_weight_kg = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    delivery_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    window_earliest_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    window_latest_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deliveries", x => x.id);
                    table.CheckConstraint("ck_deliveries_delivery_window", "window_earliest_at IS NULL OR window_latest_at IS NULL OR window_earliest_at < window_latest_at");
                    table.CheckConstraint("ck_deliveries_package_weight_kg", "package_weight_kg > 0");
                    table.ForeignKey(
                        name: "fk_deliveries_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deliveries_drivers_driver_id",
                        column: x => x.driver_id,
                        principalTable: "drivers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    driver_id = table.Column<int>(type: "integer", nullable: false),
                    sender_user_id = table.Column<int>(type: "integer", nullable: true),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_messages_drivers_driver_id",
                        column: x => x.driver_id,
                        principalTable: "drivers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shifts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    driver_id = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shifts", x => x.id);
                    table.ForeignKey(
                        name: "fk_shifts_drivers_driver_id",
                        column: x => x.driver_id,
                        principalTable: "drivers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_attempts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    delivery_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recipient = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_attempts_deliveries_delivery_id",
                        column: x => x.delivery_id,
                        principalTable: "deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "proof_of_deliveries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    delivery_id = table.Column<int>(type: "integer", nullable: false),
                    recipient_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    capture_latitude = table.Column<double>(type: "double precision", nullable: false),
                    capture_longitude = table.Column<double>(type: "double precision", nullable: false),
                    capture_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    capture_address_resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    captured_by_user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proof_of_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_proof_of_deliveries_deliveries_delivery_id",
                        column: x => x.delivery_id,
                        principalTable: "deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reviews",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    delivery_id = table.Column<int>(type: "integer", nullable: false),
                    client_id = table.Column<int>(type: "integer", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reviews", x => x.id);
                    table.CheckConstraint("ck_reviews_rating", "rating >= 1 AND rating <= 5");
                    table.ForeignKey(
                        name: "fk_reviews_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_reviews_deliveries_delivery_id",
                        column: x => x.delivery_id,
                        principalTable: "deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timeline_entries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    delivery_id = table.Column<int>(type: "integer", nullable: false),
                    actor_user_id = table.Column<int>(type: "integer", nullable: true),
                    actor_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    actor_role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    previous_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    new_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_timeline_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_timeline_entries_deliveries_delivery_id",
                        column: x => x.delivery_id,
                        principalTable: "deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "proof_assets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    proof_of_delivery_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proof_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_proof_assets_proof_of_deliveries_proof_of_delivery_id",
                        column: x => x.proof_of_delivery_id,
                        principalTable: "proof_of_deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_role_claims_role_id",
                table: "asp_net_role_claims",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "role_name_index",
                table: "asp_net_roles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_claims_user_id",
                table: "asp_net_user_claims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_logins_user_id",
                table: "asp_net_user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_roles_role_id",
                table: "asp_net_user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_roles_user_id",
                table: "asp_net_user_roles",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "email_index",
                table: "asp_net_users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "user_name_index",
                table: "asp_net_users",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clients_user_id",
                table: "clients",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_client_id",
                table: "deliveries",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_driver_id",
                table: "deliveries",
                column: "driver_id");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_status",
                table: "deliveries",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_drivers_user_id",
                table: "drivers",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_drivers_vehicle_id",
                table: "drivers",
                column: "vehicle_id",
                unique: true,
                filter: "vehicle_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_driver_id_sent_at",
                table: "messages",
                columns: new[] { "driver_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_sender_user_id",
                table: "messages",
                column: "sender_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_attempts_delivery_id",
                table: "notification_attempts",
                column: "delivery_id");

            migrationBuilder.CreateIndex(
                name: "ix_proof_assets_proof_of_delivery_id",
                table: "proof_assets",
                column: "proof_of_delivery_id");

            migrationBuilder.CreateIndex(
                name: "ix_proof_of_deliveries_captured_by_user_id",
                table: "proof_of_deliveries",
                column: "captured_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_proof_of_deliveries_delivery_id",
                table: "proof_of_deliveries",
                column: "delivery_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reviews_client_id",
                table: "reviews",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_reviews_delivery_id",
                table: "reviews",
                column: "delivery_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shifts_driver_id_open",
                table: "shifts",
                column: "driver_id",
                unique: true,
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_timeline_entries_actor_user_id",
                table: "timeline_entries",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_timeline_entries_delivery_id_occurred_at",
                table: "timeline_entries",
                columns: new[] { "delivery_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vehicles_license_plate",
                table: "vehicles",
                column: "license_plate",
                unique: true);

            // ----------------------------------------------------------------------------
            // Foreign keys onto the Identity user table, written by hand.
            //
            // Every one of these five columns holds a strongly-typed UserId (AD-22) stored as
            // an integer, while IdentityUser<int> fixes the principal key as a plain int. EF
            // Core refuses a relationship whose foreign key and principal key have different
            // CLR types, so it cannot generate these. Giving up the typed id to satisfy the
            // tooling would give up the one thing it is for - a user id and a driver row id
            // being impossible to confuse - so the constraints are declared here instead.
            // Under AD-20 the migration is the schema authority anyway; this is the authority
            // speaking directly. What is not negotiable is that all five exist: an undeclared
            // foreign key is the defect this rewrite was written to retire.
            // ----------------------------------------------------------------------------

            // A client row is the user, so it cannot outlive it.
            migrationBuilder.Sql("""
                ALTER TABLE clients
                    ADD CONSTRAINT fk_clients_asp_net_users_user_id
                    FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE;
                """);

            // Same for a driver row (FR-39: deleting the user must not raise).
            migrationBuilder.Sql("""
                ALTER TABLE drivers
                    ADD CONSTRAINT fk_drivers_asp_net_users_user_id
                    FOREIGN KEY (user_id) REFERENCES asp_net_users (id) ON DELETE CASCADE;
                """);

            // AD-20's actor rule: set null, never restrict and never cascade. Restrict would
            // make every user who has ever acted permanently undeletable - the exact "deleting
            // a driver raises a database error" defect this rewrite exists to fix - and cascade
            // would erase the history. The snapshotted name and role on the row are what keep
            // the entry readable once the reference is gone.
            migrationBuilder.Sql("""
                ALTER TABLE timeline_entries
                    ADD CONSTRAINT fk_timeline_entries_asp_net_users_actor_user_id
                    FOREIGN KEY (actor_user_id) REFERENCES asp_net_users (id) ON DELETE SET NULL;
                """);

            // The sender of a chat message, on the same reasoning: cascade would delete half a
            // conversation to close one account, and restrict would make closing it impossible.
            // There is no snapshot here - the spine permits that denormalization only on the
            // timeline - so an orphaned line reads as unattributed rather than as someone else.
            migrationBuilder.Sql("""
                ALTER TABLE messages
                    ADD CONSTRAINT fk_messages_asp_net_users_sender_user_id
                    FOREIGN KEY (sender_user_id) REFERENCES asp_net_users (id) ON DELETE SET NULL;
                """);

            // And whoever captured a proof of delivery. Cascade would destroy the evidence that
            // a delivery was completed because the driver later left.
            migrationBuilder.Sql("""
                ALTER TABLE proof_of_deliveries
                    ADD CONSTRAINT fk_proof_of_deliveries_asp_net_users_captured_by_user_id
                    FOREIGN KEY (captured_by_user_id) REFERENCES asp_net_users (id) ON DELETE SET NULL;
                """);

            // ----------------------------------------------------------------------------
            // AD-27: the timeline is append-only, guarded at the schema level.
            //
            // EF model configuration cannot enforce this - that is a code claim wearing schema
            // clothing, which AD-20 forbids - and a REVOKE would be theatre here, because the
            // application connects as the database owner in compose.yaml and an owner can
            // re-grant to itself. A raising trigger is the guard that actually holds.
            //
            // It tells an edit from a cascade by trigger depth. A statement issued directly
            // fires this trigger at pg_trigger_depth() = 1; the same statement issued by a
            // foreign key's referential-integrity trigger fires it at depth 2. Raising only at
            // depth 1 or less therefore permits DR-9's cascade from the delivery and the
            // set-null of actor_user_id above, while rejecting every direct UPDATE and DELETE.
            //
            // ERRCODE 23514 is deliberate: story 1.3 translates check violations into a typed
            // exception, so a stray write surfaces as a conflict rather than a 500. CONSTRAINT
            // and TABLE travel with it so that translation has a stable name to key on - left
            // out, PostgresException.ConstraintName is null and the only thing a caller can
            // match on is the message text.
            // ----------------------------------------------------------------------------
            migrationBuilder.Sql("""
                CREATE FUNCTION timeline_entries_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF pg_trigger_depth() <= 1 THEN
                        RAISE EXCEPTION 'timeline_entries is append-only: % is permitted only through a declared cascade', TG_OP
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'ck_timeline_entries_append_only',
                                  TABLE = 'timeline_entries';
                    END IF;
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END; $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER timeline_entries_no_update
                    BEFORE UPDATE ON timeline_entries
                    FOR EACH ROW EXECUTE FUNCTION timeline_entries_append_only();
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER timeline_entries_no_delete
                    BEFORE DELETE ON timeline_entries
                    FOR EACH ROW EXECUTE FUNCTION timeline_entries_append_only();
                """);

            // TRUNCATE fires neither row trigger above - it empties the table without ever
            // producing a row - so without this the guard is a lock on a door with no wall.
            // A truncate is always a direct statement, so it is always refused.
            migrationBuilder.Sql("""
                CREATE TRIGGER timeline_entries_no_truncate
                    BEFORE TRUNCATE ON timeline_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION timeline_entries_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The hand-written half of Up comes off first: a trigger goes with its table, but
            // the function it calls does not, and the five foreign keys are named here because
            // EF does not know they exist.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS timeline_entries_no_truncate ON timeline_entries;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS timeline_entries_no_delete ON timeline_entries;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS timeline_entries_no_update ON timeline_entries;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS timeline_entries_append_only();");

            migrationBuilder.Sql(
                "ALTER TABLE proof_of_deliveries "
                + "DROP CONSTRAINT IF EXISTS fk_proof_of_deliveries_asp_net_users_captured_by_user_id;");
            migrationBuilder.Sql(
                "ALTER TABLE messages DROP CONSTRAINT IF EXISTS fk_messages_asp_net_users_sender_user_id;");
            migrationBuilder.Sql(
                "ALTER TABLE timeline_entries "
                + "DROP CONSTRAINT IF EXISTS fk_timeline_entries_asp_net_users_actor_user_id;");
            migrationBuilder.Sql(
                "ALTER TABLE drivers DROP CONSTRAINT IF EXISTS fk_drivers_asp_net_users_user_id;");
            migrationBuilder.Sql(
                "ALTER TABLE clients DROP CONSTRAINT IF EXISTS fk_clients_asp_net_users_user_id;");

            migrationBuilder.DropTable(
                name: "asp_net_role_claims");

            migrationBuilder.DropTable(
                name: "asp_net_user_claims");

            migrationBuilder.DropTable(
                name: "asp_net_user_logins");

            migrationBuilder.DropTable(
                name: "asp_net_user_roles");

            migrationBuilder.DropTable(
                name: "asp_net_user_tokens");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "notification_attempts");

            migrationBuilder.DropTable(
                name: "proof_assets");

            migrationBuilder.DropTable(
                name: "reviews");

            migrationBuilder.DropTable(
                name: "shifts");

            migrationBuilder.DropTable(
                name: "timeline_entries");

            migrationBuilder.DropTable(
                name: "asp_net_roles");

            migrationBuilder.DropTable(
                name: "asp_net_users");

            migrationBuilder.DropTable(
                name: "proof_of_deliveries");

            migrationBuilder.DropTable(
                name: "deliveries");

            migrationBuilder.DropTable(
                name: "clients");

            migrationBuilder.DropTable(
                name: "drivers");

            migrationBuilder.DropTable(
                name: "vehicles");
        }
    }
}
