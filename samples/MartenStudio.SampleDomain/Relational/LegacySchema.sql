-- The Marten Studio sample's "legacy" schema: a small, hand-made relational database that lives in the
-- same Postgres as the Marten store and exercises every catalog edge case the studio's database browser
-- has to get right. Each object says in a comment which case it is there for.
--
-- Idempotent: it runs on every start of the sample host, inside one transaction, after Marten has seeded
-- its documents. Nothing here is ever dropped. Objects Postgres has no IF NOT EXISTS for (types, domains,
-- triggers, constraints added after the fact) are created inside DO blocks that look in pg_catalog first.
--
-- Only ever against a `legacy` schema the sample created: the COMMENT ON SCHEMA at the bottom begins with
-- the marker RelationalDemoSchema.LegacySchemaMarker, and RelationalDemoSchema runs this script only when
-- the schema does not exist yet or carries that marker. A `legacy` schema anybody else made is not
-- touched, and neither is studio_sample.app_settings, which this script also creates.
--
-- Two objects reach into the Marten document schema, `studio_sample`, and both are guarded so that the
-- script still runs against a database where Marten has not created it:
--   * legacy.customer_credit gets its foreign key to studio_sample.mt_doc_customer only once that table
--     exists - and gets it back on the next start whenever it is missing. Marten does not keep it: a
--     Weasel migration that rebuilds mt_doc_customer's primary key or the table itself does so with
--     CASCADE, which drops every foreign key that points at it, and re-creates only the ones Marten
--     declared. This one is not Marten's, so this script is what re-creates it;
--   * studio_sample.app_settings - an ordinary table inside a Marten schema - is created only once that
--     schema exists. Marten's migrations diff only the tables Marten declares, so they neither drop nor
--     alter this one.
-- The rows are written by RelationalDemoSchema.cs, not here - with one exception, the dangling order
-- line below, which has to exist before the NOT VALID foreign key it violates.

CREATE SCHEMA IF NOT EXISTS legacy;

-- ---------------------------------------------------------------------------------------------------
-- Types: an enum a column uses, a composite type a column uses, and a domain with a CHECK.
-- ---------------------------------------------------------------------------------------------------

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
                 WHERE n.nspname = 'legacy' AND t.typname = 'order_status') THEN
    CREATE TYPE legacy.order_status AS ENUM ('draft', 'submitted', 'approved', 'shipped', 'cancelled');
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
                 WHERE n.nspname = 'legacy' AND t.typname = 'postal_address') THEN
    CREATE TYPE legacy.postal_address AS (
      street TEXT,
      city TEXT,
      postal_code TEXT,
      country CHAR(2)
    );
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
                 WHERE n.nspname = 'legacy' AND t.typname = 'email_address') THEN
    CREATE DOMAIN legacy.email_address AS TEXT
      CONSTRAINT email_address_shape CHECK (VALUE ~ '^[^@[:space:]]+@[^@[:space:]]+[.][^@[:space:]]+$');
  END IF;
END $$;

-- A standalone sequence: nothing owns it, and a column default draws from it.
CREATE SEQUENCE IF NOT EXISTS legacy.invoice_number_seq START WITH 100000;

-- ---------------------------------------------------------------------------------------------------
-- Tables
-- ---------------------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS legacy.departments
  (
    department_code TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    cost_centre TEXT NOT NULL
);

-- serial: a sequence owned by the column (pg_depend deptype 'a'). manager_id is a self-referencing
-- foreign key, and email is a domain.
CREATE TABLE IF NOT EXISTS legacy.employees
  (
    employee_id SERIAL PRIMARY KEY,
    department_code TEXT NOT NULL REFERENCES legacy.departments (department_code),
    manager_id INTEGER NULL REFERENCES legacy.employees (employee_id),
    first_name TEXT NOT NULL,
    last_name TEXT NOT NULL,
    email legacy.email_address NOT NULL,
    hired_on DATE NOT NULL,
    terminated_on DATE NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- A composite primary key, and a column of a composite type.
CREATE TABLE IF NOT EXISTS legacy.warehouses
  (
    region_code CHAR(2) NOT NULL,
    warehouse_no SMALLINT NOT NULL,
    name TEXT NOT NULL,
    address legacy.postal_address NULL,
    opened_on DATE NOT NULL,
    PRIMARY KEY (region_code, warehouse_no)
);

-- Forty columns, the shape of a table somebody imported from a spreadsheet and never normalised.
CREATE TABLE IF NOT EXISTS legacy.product_catalogue
  (
    sku TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NULL,
    category TEXT NULL,
    subcategory TEXT NULL,
    brand TEXT NULL,
    supplier_code TEXT NULL,
    ean CHAR(13) NULL,
    upc CHAR(12) NULL,
    colour TEXT NULL,
    size_label TEXT NULL,
    material TEXT NULL,
    weight_g INTEGER NULL,
    length_mm INTEGER NULL,
    width_mm INTEGER NULL,
    height_mm INTEGER NULL,
    unit_price NUMERIC(10, 2) NULL,
    cost_price NUMERIC(10, 2) NULL,
    currency CHAR(3) NULL,
    vat_rate NUMERIC(4, 2) NULL,
    stock_qty INTEGER NULL,
    reorder_level INTEGER NULL,
    lead_time_days SMALLINT NULL,
    discontinued BOOLEAN NOT NULL DEFAULT FALSE,
    launched_on DATE NULL,
    country_of_origin CHAR(2) NULL,
    hs_code TEXT NULL,
    warranty_months SMALLINT NULL,
    min_order_qty INTEGER NULL,
    pack_size INTEGER NULL,
    shelf_life_days INTEGER NULL,
    hazardous BOOLEAN NOT NULL DEFAULT FALSE,
    fragile BOOLEAN NOT NULL DEFAULT FALSE,
    image_url TEXT NULL,
    notes TEXT NULL,
    created_by TEXT NULL,
    created_at TIMESTAMPTZ NULL,
    modified_by TEXT NULL,
    modified_at TIMESTAMPTZ NULL,
    import_batch TEXT NULL
);

-- A three-column primary key and a two-column foreign key.
CREATE TABLE IF NOT EXISTS legacy.stock_levels
  (
    region_code CHAR(2) NOT NULL,
    warehouse_no SMALLINT NOT NULL,
    sku TEXT NOT NULL REFERENCES legacy.product_catalogue (sku),
    quantity INTEGER NOT NULL,
    counted_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (region_code, warehouse_no, sku),
    FOREIGN KEY (region_code, warehouse_no) REFERENCES legacy.warehouses (region_code, warehouse_no)
);

-- An identity column (a sequence owned through pg_depend deptype 'i'), an enum column, and a default
-- drawn from the standalone sequence.
CREATE TABLE IF NOT EXISTS legacy.purchase_orders
  (
    order_id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    reference TEXT NOT NULL UNIQUE,
    supplier_name TEXT NOT NULL,
    status legacy.order_status NOT NULL DEFAULT 'draft',
    requested_by INTEGER NULL REFERENCES legacy.employees (employee_id),
    ordered_on DATE NOT NULL,
    total NUMERIC(12, 2) NOT NULL,
    invoice_number BIGINT NOT NULL DEFAULT nextval('legacy.invoice_number_seq'),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- The foreign key to purchase_orders is added NOT VALID further down, after the one row that violates it.
CREATE TABLE IF NOT EXISTS legacy.purchase_order_lines
  (
    order_id BIGINT NOT NULL,
    line_no SMALLINT NOT NULL,
    sku TEXT NOT NULL,
    quantity INTEGER NOT NULL,
    unit_price NUMERIC(10, 2) NOT NULL,
    PRIMARY KEY (order_id, line_no)
);

-- No primary key and no unique index: an append-only audit table, the case that has to page on ctid.
CREATE TABLE IF NOT EXISTS legacy.audit_log
  (
    logged_at TIMESTAMPTZ NOT NULL,
    actor TEXT NOT NULL,
    action TEXT NOT NULL,
    entity TEXT NOT NULL,
    entity_key TEXT NULL,
    details TEXT NULL
);

-- jsonb, a long text column stored EXTERNAL (out of line and uncompressed once a value passes the TOAST
-- threshold, so substring() reads only the pages it needs), bytea, timestamptz and timestamp without
-- a time zone. A citext column is added below only when the citext extension is already installed.
CREATE TABLE IF NOT EXISTS legacy.integration_messages
  (
    message_id UUID PRIMARY KEY,
    channel TEXT NOT NULL,
    payload JSONB NOT NULL,
    raw_body TEXT NULL,
    attachment BYTEA NULL,
    received_at TIMESTAMPTZ NOT NULL,
    sent_at_local TIMESTAMP NULL
);

DO $$
BEGIN
  IF (SELECT attstorage FROM pg_catalog.pg_attribute
      WHERE attrelid = 'legacy.integration_messages'::regclass AND attname = 'raw_body') <> 'e' THEN
    ALTER TABLE legacy.integration_messages ALTER COLUMN raw_body SET STORAGE EXTERNAL;
  END IF;
END $$;

-- citext only if somebody already installed it: the demo does not install extensions.
DO $$
DECLARE
  citext_schema TEXT;
BEGIN
  SELECT n.nspname INTO citext_schema
  FROM pg_catalog.pg_extension e JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
  WHERE e.extname = 'citext';

  IF citext_schema IS NOT NULL AND NOT EXISTS (
      SELECT 1 FROM pg_catalog.pg_attribute
      WHERE attrelid = 'legacy.integration_messages'::regclass AND attname = 'sender' AND NOT attisdropped) THEN
    EXECUTE format('ALTER TABLE legacy.integration_messages ADD COLUMN sender %I.citext NULL', citext_schema);
  END IF;
END $$;

-- Declaratively partitioned by range, with two partitions.
CREATE TABLE IF NOT EXISTS legacy.sensor_readings
  (
    sensor_id TEXT NOT NULL,
    read_at TIMESTAMPTZ NOT NULL,
    reading NUMERIC(8, 3) NOT NULL,
    unit TEXT NOT NULL,
    PRIMARY KEY (sensor_id, read_at)
) PARTITION BY RANGE (read_at);

CREATE TABLE IF NOT EXISTS legacy.sensor_readings_2026_h1 PARTITION OF legacy.sensor_readings
  FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2026-07-01 00:00:00+00');

CREATE TABLE IF NOT EXISTS legacy.sensor_readings_2026_h2 PARTITION OF legacy.sensor_readings
  FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2027-01-01 00:00:00+00');

-- Keyed by the Marten customer's id; the foreign key into the Marten document table is added below.
CREATE TABLE IF NOT EXISTS legacy.customer_credit
  (
    customer_id UUID PRIMARY KEY,
    credit_limit NUMERIC(12, 2) NOT NULL,
    currency CHAR(3) NOT NULL DEFAULT 'EUR',
    risk_band TEXT NOT NULL,
    reviewed_at TIMESTAMPTZ NULL
);

-- ---------------------------------------------------------------------------------------------------
-- Foreign keys added after the fact
-- ---------------------------------------------------------------------------------------------------

-- NOT VALID, with one row that violates it: the line of an order the old system purged. The row goes in
-- first, because NOT VALID only excuses rows that already exist - every later insert is checked.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
                 WHERE conrelid = 'legacy.purchase_order_lines'::regclass
                   AND conname = 'purchase_order_lines_order_id_fkey') THEN
    INSERT INTO legacy.purchase_order_lines (order_id, line_no, sku, quantity, unit_price)
    VALUES (9001, 1, 'SKU-999', 4, 12.50)
    ON CONFLICT DO NOTHING;

    ALTER TABLE legacy.purchase_order_lines
      ADD CONSTRAINT purchase_order_lines_order_id_fkey
      FOREIGN KEY (order_id) REFERENCES legacy.purchase_orders (order_id) NOT VALID;
  END IF;
END $$;

-- From a plain table into a Marten document table. ON DELETE CASCADE, so that nothing Marten does to a
-- customer - the studio's delete, the demo-data truncation - is refused because of it. A credit row
-- whose customer is gone is removed first, so the constraint can always be (re)validated.
DO $$
BEGIN
  IF to_regclass('studio_sample.mt_doc_customer') IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
                     WHERE conrelid = 'legacy.customer_credit'::regclass
                       AND conname = 'customer_credit_customer_id_fkey') THEN
    DELETE FROM legacy.customer_credit c
    WHERE NOT EXISTS (SELECT 1 FROM studio_sample.mt_doc_customer d WHERE d.id = c.customer_id);

    ALTER TABLE legacy.customer_credit
      ADD CONSTRAINT customer_credit_customer_id_fkey
      FOREIGN KEY (customer_id) REFERENCES studio_sample.mt_doc_customer (id) ON DELETE CASCADE;
  END IF;
END $$;

-- An ordinary table inside the Marten document schema.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = 'studio_sample') THEN
    CREATE TABLE IF NOT EXISTS studio_sample.app_settings
      (
        setting_key TEXT PRIMARY KEY,
        setting_value TEXT NOT NULL,
        value_type TEXT NOT NULL,
        updated_by TEXT NULL,
        updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
    );

    COMMENT ON TABLE studio_sample.app_settings IS 'Application settings the host keeps beside its Marten documents. A plain table: Marten did not create it and will not drop it.';
  END IF;
END $$;

-- ---------------------------------------------------------------------------------------------------
-- Routines: an overloaded function, a procedure, an aggregate and a trigger function
-- ---------------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION legacy.format_employee_name(first_name TEXT, last_name TEXT)
RETURNS TEXT
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT last_name || ', ' || first_name
$$;

CREATE OR REPLACE FUNCTION legacy.format_employee_name(employee_id INTEGER)
RETURNS TEXT
LANGUAGE sql
STABLE
AS $$
  SELECT legacy.format_employee_name(e.first_name, e.last_name)
  FROM legacy.employees e
  WHERE e.employee_id = format_employee_name.employee_id
$$;

-- SECURITY DEFINER with no search_path: the kind of routine an audit flags, kept here so it can be.
CREATE OR REPLACE PROCEDURE legacy.archive_audit_log(older_than TIMESTAMPTZ)
LANGUAGE sql
SECURITY DEFINER
AS $$
  DELETE FROM legacy.audit_log WHERE logged_at < older_than
$$;

CREATE OR REPLACE AGGREGATE legacy.compound_growth(NUMERIC) (
  SFUNC = pg_catalog.numeric_mul,
  STYPE = NUMERIC,
  INITCOND = '1'
);

CREATE OR REPLACE FUNCTION legacy.touch_updated_at()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
  NEW.updated_at := now();
  RETURN NEW;
END
$$;

-- ---------------------------------------------------------------------------------------------------
-- Triggers: one enabled, one disabled
-- ---------------------------------------------------------------------------------------------------

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                 WHERE tgrelid = 'legacy.employees'::regclass AND tgname = 'employees_touch_updated_at') THEN
    CREATE TRIGGER employees_touch_updated_at
      BEFORE UPDATE ON legacy.employees
      FOR EACH ROW EXECUTE FUNCTION legacy.touch_updated_at();
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                 WHERE tgrelid = 'legacy.purchase_orders'::regclass AND tgname = 'purchase_orders_touch_updated_at') THEN
    CREATE TRIGGER purchase_orders_touch_updated_at
      BEFORE UPDATE ON legacy.purchase_orders
      FOR EACH ROW EXECUTE FUNCTION legacy.touch_updated_at();

    ALTER TABLE legacy.purchase_orders DISABLE TRIGGER purchase_orders_touch_updated_at;
  END IF;
END $$;

-- ---------------------------------------------------------------------------------------------------
-- Views: a plain view, a populated materialized view, and one that has never been refreshed
-- ---------------------------------------------------------------------------------------------------

CREATE OR REPLACE VIEW legacy.active_employees AS
  SELECT e.employee_id,
         legacy.format_employee_name(e.first_name, e.last_name) AS display_name,
         e.email,
         d.name AS department,
         legacy.format_employee_name(m.first_name, m.last_name) AS manager,
         e.hired_on
  FROM legacy.employees e
  JOIN legacy.departments d ON d.department_code = e.department_code
  LEFT JOIN legacy.employees m ON m.employee_id = e.manager_id
  WHERE e.terminated_on IS NULL;

-- Populated when it is created and refreshed by the seeder after it writes stock.
CREATE MATERIALIZED VIEW IF NOT EXISTS legacy.stock_by_region AS
  SELECT w.region_code,
         count(DISTINCT w.warehouse_no) AS warehouses,
         count(s.sku) AS stock_lines,
         coalesce(sum(s.quantity), 0) AS units
  FROM legacy.warehouses w
  LEFT JOIN legacy.stock_levels s ON s.region_code = w.region_code AND s.warehouse_no = w.warehouse_no
  GROUP BY w.region_code
WITH DATA;

-- Never populated, on purpose: reading it raises 55000 until somebody refreshes it, and nothing here does.
CREATE MATERIALIZED VIEW IF NOT EXISTS legacy.monthly_order_totals AS
  SELECT date_trunc('month', o.ordered_on)::date AS month,
         o.status,
         count(*) AS orders,
         sum(o.total) AS total
  FROM legacy.purchase_orders o
  GROUP BY 1, 2
WITH NO DATA;

-- ---------------------------------------------------------------------------------------------------
-- Comments
-- ---------------------------------------------------------------------------------------------------

COMMENT ON SCHEMA legacy IS 'marten-studio-sample:legacy - A hand-made relational schema beside the Marten store, covering every catalog edge case the database browser has to handle.';
COMMENT ON TABLE legacy.employees IS 'Staff, with a self-referencing manager key and a domain-typed e-mail address.';
COMMENT ON COLUMN legacy.employees.manager_id IS 'The employee this one reports to; NULL for the managing director.';
COMMENT ON COLUMN legacy.employees.email IS 'legacy.email_address, a domain with a CHECK.';
COMMENT ON TABLE legacy.audit_log IS 'Append-only, with no primary key and no unique index. Two rows are exact duplicates, as they are in the real thing.';
COMMENT ON TABLE legacy.purchase_order_lines IS 'Its foreign key to purchase_orders is NOT VALID: order 9001 was purged by the old system and its line was not.';
COMMENT ON COLUMN legacy.purchase_orders.invoice_number IS 'Drawn from legacy.invoice_number_seq, a sequence no column owns.';
COMMENT ON TABLE legacy.integration_messages IS 'Messages from partner systems: jsonb, TOASTed text, bytea and both kinds of timestamp. Has a citext sender column only when the citext extension was installed before the demo schema was applied.';
COMMENT ON COLUMN legacy.integration_messages.raw_body IS 'Stored EXTERNAL; several values are over 10 KB.';
COMMENT ON COLUMN legacy.integration_messages.sent_at_local IS 'timestamp without time zone, in whatever zone the sender was in.';
COMMENT ON TABLE legacy.sensor_readings IS 'Range-partitioned by read_at, one partition per half-year.';
COMMENT ON TABLE legacy.customer_credit IS 'Credit limits the old billing system still owns, keyed by the Marten customer document''s id.';
COMMENT ON MATERIALIZED VIEW legacy.monthly_order_totals IS 'Created WITH NO DATA and never refreshed.';
