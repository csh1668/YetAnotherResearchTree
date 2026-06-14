PROJECT_DIR := Source/YART
PROJECT_FILE := $(PROJECT_DIR)/YART.csproj
SOLUTION_FILE := $(PROJECT_DIR)/YART.sln
OUTPUT_DIR := Assemblies
OUTPUT_FILE := $(OUTPUT_DIR)/YART.dll
DOTNET := dotnet
CONFIGURATION := Release-1.6

.PHONY: all build clean rebuild help

all: build
build: $(OUTPUT_FILE)

$(OUTPUT_FILE): $(PROJECT_FILE)
	$(DOTNET) build $(PROJECT_FILE) -c $(CONFIGURATION) --no-incremental

clean:
	$(DOTNET) clean $(PROJECT_FILE) -c $(CONFIGURATION)