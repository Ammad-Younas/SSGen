-- Which captured screens are on the Design page. Exploration captures arrive outside Design and are moved in
-- with "Continue to Design"; manual captures go straight in. Screens that already existed stay in Design.
ALTER TABLE screens ADD COLUMN in_design INTEGER NOT NULL DEFAULT 1;
