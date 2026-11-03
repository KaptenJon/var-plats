\connect varplats
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE TABLE IF NOT EXISTS reports (
    "Id" uuid PRIMARY KEY,
    "Title" varchar(100) NOT NULL,
    "Description" varchar(1000) NOT NULL,
    "Category" integer NOT NULL CHECK ("Category" BETWEEN 0 AND 2),
    "Location" geography(Point,4326) NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "Status" integer NOT NULL DEFAULT 0 CHECK ("Status" BETWEEN 0 AND 2),
    CONSTRAINT report_title_length CHECK (length(trim("Title")) >= 5),
    CONSTRAINT report_description_length CHECK (length(trim("Description")) >= 10)
);
CREATE INDEX IF NOT EXISTS "IX_reports_Location" ON reports USING gist ("Location");
